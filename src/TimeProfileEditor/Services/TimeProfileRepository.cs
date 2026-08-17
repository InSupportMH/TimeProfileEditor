using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TimeProfileEditor.Model;
using TimeProfileEditor.Security;
using VideoOS.Platform;
using VideoOS.Platform.ConfigurationItems;

namespace TimeProfileEditor.Services
{
    /// <summary>
    /// Reads and writes time profiles through the MIP Configuration API.
    ///
    /// Every call runs as the logged-in Smart Client user, which is the point: the Management
    /// Server applies that user's role permissions to each write and rejects the ones it must.
    /// That server-side refusal - not the disabled Save button - is what actually protects the
    /// configuration, and it holds even if the client binary is tampered with.
    /// </summary>
    internal sealed class TimeProfileRepository
    {
        private const string FrequencyWeekly = "Weekly";
        private const string PatternTypeExplicit = "Explicit";
        private const string RangeNoLimit = "NoLimit";
        private const string RangeLimitByDate = "LimitByDate";
        private const string OccurrenceNone = "None";

        /// <summary>
        /// What goes in RecurrenceRangeMaxOccurrences when nothing is limited by a count.
        ///
        /// The field is inert for everything this plugin writes - the range is either open
        /// (<see cref="RangeNoLimit"/>) or bounded by a date (<see cref="RangeLimitByDate"/>), and
        /// neither consults it. But it is still validated on the way in, and not by every server:
        /// a 2025 R2 Professional+ Management Server refuses 0 with "The RangeMaxOccurrences
        /// property cannot be set to a value outside the range of 1 - 999", while the lab server
        /// the write tests run against accepts it. Emitting a value that is valid everywhere costs
        /// nothing, so this is not conditional on which server answered.
        ///
        /// The ceiling rather than the floor, deliberately. If some server does honour the field,
        /// 1 would end a weekly pattern after its first occurrence - a schedule that quietly stops
        /// applying, which is the failure nobody notices. 999 covers about nineteen years, and
        /// being wrong in that direction is visible.
        /// </summary>
        private const int MaxOccurrencesPlaceholder = 999;

        /// <summary>
        /// Longest interval this plugin will write.
        ///
        /// Deliberately one minute short of a full day. The Management Server parses the duration
        /// string with day semantics, so "24:00:00" is read back as twenty-four *days* - the server
        /// itself then describes the appointment as "from 00:00 for 24 days". Anything from 24:00:00
        /// upwards is therefore unsafe to emit, and a whole-day interval is expressed as 00:00-23:59.
        /// </summary>
        public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1);

        private static ServerId ServerId =>
            EnvironmentManager.Instance.MasterSite?.ServerId ?? Configuration.Instance.ServerFQID.ServerId;

        /// <summary>
        /// How the client-side gate is evaluated. Production never replaces this; the harness does,
        /// so the diff and write path can be exercised on a server where the plugin's security
        /// namespace has not been registered yet. Swapping it does not widen access - the
        /// Management Server still applies the signed-in user's role to every call below.
        /// </summary>
        internal Func<PermissionState> PermissionCheck { get; set; } = PluginSecurity.CanEdit;

        public IReadOnlyList<TimeProfileInfo> LoadProfiles()
        {
            var folder = GetFolder(refresh: true);
            return folder.TimeProfiles
                .Select(tp => new TimeProfileInfo
                {
                    Id = tp.Guid,
                    Name = tp.Name,
                    Description = tp.Description,
                    ProfileType = tp.TimeProfileType
                })
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public ProfileSchedule LoadSchedule(Guid profileId)
        {
            var tp = FindProfile(profileId, refresh: true);
            if (tp == null) return null;

            var schedule = new ProfileSchedule
            {
                Profile = new TimeProfileInfo
                {
                    Id = tp.Guid,
                    Name = tp.Name,
                    Description = tp.Description,
                    ProfileType = tp.TimeProfileType
                },
                LastModified = tp.LastModified
            };

            foreach (var item in tp.TimeProfileAppointmentRecurChildItems)
                schedule.Entries.Add(ToEntry(item));

            foreach (var item in tp.TimeProfileAppointmentRootChildItems)
                schedule.Entries.Add(ToEntry(item));

            return schedule;
        }

        private static ScheduleEntry ToEntry(TimeProfileAppointmentRecurChildItem item)
        {
            var start = ParseTime(item.RecurrenceOccurrenceStartTime);
            var duration = ParseTime(item.RecurrenceOccurrenceDuration);
            var limit = item.RecurrenceRangeLimit ?? RangeNoLimit;

            // Only a plain "every week on these days" pattern can be drawn on - and safely
            // written back from - a seven-day grid. Anything else is shown as the server
            // describes it and left exactly as it is. A run limited by a number of occurrences
            // is excluded too: the editor has no way to express "the next 10 times", and
            // rewriting it as an open-ended pattern would quietly change what it means.
            var isPlainWeekly =
                string.Equals(item.RecurrencePatternFrequency, FrequencyWeekly, StringComparison.OrdinalIgnoreCase) &&
                item.RecurrencePatternInterval == 1 &&
                duration <= MaxDuration &&
                (limit.Equals(RangeNoLimit, StringComparison.OrdinalIgnoreCase) ||
                 limit.Equals(RangeLimitByDate, StringComparison.OrdinalIgnoreCase));

            return new ScheduleEntry
            {
                AppointmentRootId = item.AppointmentRootId,
                Kind = isPlainWeekly ? ScheduleEntryKind.Weekly : ScheduleEntryKind.OtherRecurring,
                Days = (DayFlags)item.RecurrencePatternDaysOfWeek,
                Start = start,
                Duration = duration,
                Subject = item.Subject,
                RangeStart = item.RecurrenceRangeStartDate,
                RangeEnd = limit.Equals(RangeLimitByDate, StringComparison.OrdinalIgnoreCase)
                    ? item.RecurrenceRangeEndDate.Date
                    : (DateTime?)null,
                ServerDescription = item.RecurrenceDescription
            };
        }

        private static ScheduleEntry ToEntry(TimeProfileAppointmentRootChildItem item) =>
            new ScheduleEntry
            {
                Kind = ScheduleEntryKind.SingleOccurrence,
                Subject = item.Subject,
                AllDayEvent = item.AllDayEvent,
                OccurrenceStart = item.StartDateTime,
                OccurrenceEnd = item.EndDateTime,
                Start = item.StartDateTime.TimeOfDay,
                Duration = item.EndDateTime - item.StartDateTime,
                ServerDescription = item.AllDayEvent
                    ? $"Heldag {item.StartDateTime:yyyy-MM-dd}"
                    : $"{item.StartDateTime:yyyy-MM-dd HH:mm} - {item.EndDateTime:yyyy-MM-dd HH:mm}"
            };

        /// <summary>
        /// Applies <paramref name="desired"/> to the profile.
        ///
        /// Covers weekly patterns and one-off dates; recurrences the week grid cannot represent are
        /// never written. The diff is deliberately minimal - an entry the user did not touch
        /// generates no server call at all, which keeps the blast radius small given that the
        /// Configuration API offers no transaction to roll back a half-applied save.
        /// </summary>
        public SaveOutcome Save(Guid profileId, IReadOnlyList<ScheduleEntry> desired,
            IReadOnlyList<ScheduleEntry> baseline, DateTime expectedLastModified)
        {
            var permission = PermissionCheck();
            if (permission != PermissionState.Granted)
                return SaveOutcome.Denied(DescribePermissionProblem(permission));

            // Not found means "not in what I could read", and on its own that says nothing about
            // whether the profile exists. Reported as such rather than as a failure, so the caller
            // can ask someone who reads with more authority before concluding anything - see
            // SaveStatus.NotVisible. Nothing has been written at this point.
            var tp = FindProfile(profileId, refresh: true);
            if (tp == null)
                return SaveOutcome.NotVisible(
                    "Tidsprofilen finns inte i den konfiguration som kunde läsas.");

            if (tp.TimeProfileType != null &&
                tp.TimeProfileType.Equals("Sunclock", StringComparison.OrdinalIgnoreCase))
                return SaveOutcome.Fail("Sunclock-profiler styrs av soluppgång och solnedgång och kan inte redigeras här.");

            // Someone may have saved the same profile from Management Client while this one was open.
            if (expectedLastModified != default && tp.LastModified != default &&
                tp.LastModified > expectedLastModified)
            {
                return new SaveOutcome
                {
                    Status = SaveStatus.Conflict,
                    Message = "Tidsprofilen har ändrats av någon annan sedan du öppnade den. " +
                              "Läs om profilen och gör om dina ändringar så att inget skrivs över."
                };
            }

            // Clamp before anything compares or writes. Doing it only at write time would leave the
            // client holding a value the server can never store, so the entry would compare as
            // different on every later save and stay permanently unsaved.
            var wanted = desired
                .Where(e => e.IsEditable)
                .Select(Normalized)
                .ToList();
            var before = (baseline ?? new List<ScheduleEntry>())
                .Where(e => e.IsEditable).ToList();
            var wantedKeys = new HashSet<Guid>(wanted.Select(e => e.Key));

            // Every change is expressed as an add and/or a remove, never as an in-place edit, and
            // the plan is built purely from what the client knows.
            //
            // Editing a child item and calling TimeProfile.Save() does persist, but it is not usable
            // here: the server reissues AppointmentRootIds, so ids captured earlier no longer match
            // and a RemoveRecurringAppointment aimed at one is answered with Success while removing
            // nothing. Removals therefore resolve their target by content against a fresh read,
            // immediately before the call. Verified against the Management Server.
            var removals = before
                .Where(e => !wantedKeys.Contains(e.Key))
                .Select(e => new Change { Old = e, Label = "Borttagen: " + e.Describe() })
                .ToList();

            var byKey = before.ToDictionary(e => e.Key);
            var additions = new List<Change>();

            foreach (var entry in wanted)
            {
                if (!byKey.TryGetValue(entry.Key, out var original))
                {
                    additions.Add(new Change { New = entry, Label = "Tillagd: " + entry.Describe() });
                    continue;
                }

                if (entry.HasSameScheduleAs(original)) continue;

                additions.Add(new Change
                {
                    New = entry,
                    Label = $"Ändrad: {original.Describe()}  ->  {entry.Describe()}"
                });
                removals.Add(new Change { Old = original, Label = null });
            }

            var outcome = new SaveOutcome { Status = SaveStatus.Success };
            if (!removals.Any() && !additions.Any())
            {
                outcome.Status = SaveStatus.NothingToDo;
                outcome.Message = "Inget att spara - schemat är oförändrat.";
                return outcome;
            }

            var applied = false;
            try
            {
                // One-off dates are removed before anything is added.
                //
                // Their removal selector has to be looked up in a server-built dictionary keyed on
                // the booking's subject, so two bookings sharing a subject cannot both appear in it.
                // Adding an edited copy first would produce exactly that collision - the edited
                // booking and the original it replaces have the same subject - and the original
                // would become unaddressable. Clearing first keeps every lookup unambiguous.
                foreach (var change in removals.Where(c => c.Old.Kind == ScheduleEntryKind.SingleOccurrence))
                {
                    var task = RemoveSingleOccurrence(profileId, change.Old);
                    if (task == null) continue;

                    if (task.State != StateEnum.Success)
                        return Partial(outcome, applied, $"Kunde inte ta bort {change.Old.Describe()}: {Describe(task)}");

                    applied = true;
                    if (change.Label != null) outcome.AppliedChanges.Add(change.Label);
                }

                // Weekly patterns go the other way round - added before the old ones are removed.
                // Should the save break between the two phases, the profile is left covering
                // slightly more time than intended rather than less: a duplicated interval is
                // visible and easy to delete, whereas a missing one silently stops a rule applying.
                foreach (var change in additions)
                {
                    var entry = change.New;

                    // A fresh instance per call. The item goes stale the moment the server executes
                    // a method on it, and a second call made through the same instance is accepted
                    // and reports Success while doing nothing at all - so only the first change in
                    // a batch would land. Verified against the Management Server.
                    tp = FindProfile(profileId, refresh: true);
                    if (tp == null)
                        return Partial(outcome, applied, "Tidsprofilen försvann medan ändringarna sparades.");

                    var task = entry.Kind == ScheduleEntryKind.SingleOccurrence
                        ? AddSingleOccurrence(tp, entry)
                        : AddWeekly(tp, entry);

                    if (task.State != StateEnum.Success)
                        return Partial(outcome, applied, $"Kunde inte spara {entry.Describe()}: {Describe(task)}");

                    applied = true;
                    if (change.Label != null) outcome.AppliedChanges.Add(change.Label);
                }

                foreach (var change in removals.Where(c => c.Old.Kind != ScheduleEntryKind.SingleOccurrence))
                {
                    var task = RemoveWeekly(profileId, change.Old);

                    // Null means the entry was already gone - somebody else removed it, or it was
                    // never stored. Nothing to do, and not an error.
                    if (task == null) continue;

                    if (task.State != StateEnum.Success)
                        return Partial(outcome, applied, $"Kunde inte ta bort {change.Old.Describe()}: {Describe(task)}");

                    applied = true;
                    if (change.Label != null) outcome.AppliedChanges.Add(change.Label);
                }
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;
                if (LooksLikePermissionProblem(message))
                {
                    return new SaveOutcome
                    {
                        Status = applied ? SaveStatus.PartiallyApplied : SaveStatus.PermissionDenied,
                        Message = "Du saknar behörighet att ändra denna tidsprofil. " +
                                  "Servern nekade ändringen. Kontakta din systemadministratör."
                    };
                }

                return Partial(outcome, applied, "Det gick inte att spara ändringarna: " + message);
            }

            // Read the profile back and confirm it actually looks like what was asked for.
            //
            // Worth the extra round trip: a removal aimed at an appointment the server no longer
            // recognises is answered with Success and changes nothing, so trusting the task results
            // alone would let the plugin report "Ändringarna har sparats" over a profile that was
            // not saved. Silently claiming success is the one outcome that must not happen.
            var verification = Verify(profileId, wanted);
            if (verification != null)
            {
                outcome.Status = SaveStatus.PartiallyApplied;
                outcome.Message = verification;
            }

            return outcome;
        }

        /// <summary>Returns null when the stored schedule matches, otherwise a description of the gap.</summary>
        private string Verify(Guid profileId, IReadOnlyList<ScheduleEntry> wanted)
        {
            try
            {
                var stored = LoadSchedule(profileId)?.Entries
                    .Where(e => e.IsEditable)
                    .ToList();

                if (stored == null)
                    return "Ändringarna kunde inte bekräftas - tidsprofilen gick inte att läsa om.";

                var outstanding = stored.ToList();
                var missing = 0;
                foreach (var entry in wanted)
                {
                    var match = outstanding.FirstOrDefault(s => s.HasSameScheduleAs(entry));
                    if (match == null) missing++;
                    else outstanding.Remove(match);
                }

                if (missing == 0 && outstanding.Count == 0) return null;

                return "Servern sparade inte allt: " +
                       (missing > 0 ? $"{missing} ändring(ar) saknas" : "") +
                       (missing > 0 && outstanding.Count > 0 ? " och " : "") +
                       (outstanding.Count > 0 ? $"{outstanding.Count} tid(er) blev kvar" : "") +
                       ". Läs om profilen för att se aktuellt läge.";
            }
            catch (Exception ex)
            {
                return "Ändringarna kunde inte bekräftas: " + ex.GetBaseException().Message;
            }
        }

        private static ServerTask AddWeekly(TimeProfile tp, ScheduleEntry entry) =>
            tp.AddRecurringAppointment(
                subject: SubjectOf(entry),
                recurrenceOccurrenceStartTime: FormatTime(entry.Start),
                recurrenceOccurrenceDuration: FormatTime(entry.Duration),
                recurrencePatternFrequency: FrequencyWeekly,
                recurrencePatternInterval: 1,
                recurrencePatternDaysOfWeek: (int)entry.Days,
                recurrencePatternDayOfMonth: 1,
                recurrencePatternMonthOfYear: 1,
                recurrencePatternOccurrenceOfDayInMonth: OccurrenceNone,
                recurrencePatternType: PatternTypeExplicit,
                recurrenceRangeStartDate: entry.RangeStart,
                // The server wants an end date even when the range is open, so a placeholder goes
                // in and RecurrenceRangeLimit is what actually decides whether it counts.
                recurrenceRangeEndDate: entry.RangeEnd ?? entry.RangeStart,
                recurrenceRangeLimit: entry.RangeEnd.HasValue ? RangeLimitByDate : RangeNoLimit,
                // Same reasoning as the end date above, and for the same reason as the day-of-month
                // and month-of-year placeholders: the field is not consulted, but it is validated.
                recurrenceRangeMaxOccurrences: MaxOccurrencesPlaceholder);

        private static ServerTask AddSingleOccurrence(TimeProfile tp, ScheduleEntry entry)
        {
            var start = entry.OccurrenceStart ?? DateTime.Today;

            // An all-day booking is stored with start and end on the same midnight; the flag, not
            // the span, is what makes it cover the day. Verified against the Management Server.
            var end = entry.AllDayEvent ? start.Date : (entry.OccurrenceEnd ?? start.AddHours(1));

            return tp.AddAppointment(entry.AllDayEvent, entry.AllDayEvent ? start.Date : start, end, SubjectOf(entry));
        }

        private ServerTask RemoveWeekly(Guid profileId, ScheduleEntry target)
        {
            var tp = FindProfile(profileId, refresh: true);
            if (tp == null) return null;

            // Resolve the target inside this read - the server's ids do not survive between reads.
            // Two identical intervals are interchangeable, so the first match is the right one.
            var victim = tp.TimeProfileAppointmentRecurChildItems
                .Select(i => new { Item = i, Entry = ToEntry(i) })
                .FirstOrDefault(x => x.Entry.Kind == ScheduleEntryKind.Weekly &&
                                     x.Entry.HasSameScheduleAs(target));

            return victim == null ? null : tp.RemoveRecurringAppointment(victim.Item.AppointmentRootId);
        }

        /// <summary>
        /// Removes a one-off booking.
        ///
        /// One-off bookings carry no id of their own, so the target is chosen from a list the
        /// server produces: the parameterless RemoveAppointment() returns a task whose
        /// ItemSelectionValues maps each booking's subject to a "&lt;start ticks&gt;-&lt;position&gt;" handle.
        /// The selection is set on that same task and run with Execute(); the RemoveAppointment(string)
        /// overload is rejected, as the handle only means anything inside the invocation that issued it.
        ///
        /// Only the ticks half is sent. The trailing position is numbered within the one-off bookings
        /// alone, but the server resolves it against all of a profile's appointments - so as soon as
        /// the profile also holds a weekly pattern the two disagree and the handle the server just
        /// produced comes back "Invalid selection". The start time on its own is accepted in every
        /// case. Established by testing each variant against the Management Server.
        /// </summary>
        private ServerTask RemoveSingleOccurrence(Guid profileId, ScheduleEntry target)
        {
            if (target.OccurrenceStart == null) return null;

            var tp = FindProfile(profileId, refresh: true);
            if (tp == null) return null;

            // Read the children before opening the invocation, never after. Touching them is itself
            // a server round trip, and one made between RemoveAppointment() and Execute() voids the
            // pending selection - the call then comes back "Invalid selection" even though the
            // handle was the one the server had just handed out.
            var storedCount = tp.TimeProfileAppointmentRootChildItems?.Count ?? 0;

            var task = tp.RemoveAppointment();
            var selections = task?.ItemSelectionValues;
            if (selections == null || selections.Count == 0) return null;

            // The dictionary is keyed on subject, so bookings that share one collapse into a single
            // entry and the rest become unaddressable. That is a property of the existing data, not
            // of this save, but it is worth saying out loud - the alternative is a removal that
            // quietly does nothing and only surfaces as a mismatch in the verification afterwards.
            if (storedCount > selections.Count)
            {
                EnvironmentManager.Instance.Log(true, nameof(TimeProfileRepository),
                    $"Tidsprofilen har {storedCount} enstaka datum men servern kan bara peka ut " +
                    $"{selections.Count} av dem. Datum som delar benamning gar inte att ta bort " +
                    "forran de fatt olika benamningar.");
            }

            var wantedTicks = target.OccurrenceStart.Value.Ticks;
            var matches = selections.Where(kv => TicksOf(kv.Value) == wantedTicks).ToList();
            if (!matches.Any()) return null;

            // Prefer the one whose subject also matches, so editing only the text of a booking
            // removes that booking rather than another one starting at the same moment.
            var chosen = matches.FirstOrDefault(kv =>
                string.Equals(kv.Key, target.Subject ?? "", StringComparison.Ordinal));

            var handle = chosen.Value ?? matches.First().Value;
            task.ItemSelection = TicksOf(handle).ToString(CultureInfo.InvariantCulture);
            return task.Execute();
        }

        /// <summary>Reads the tick count out of a "&lt;ticks&gt;-&lt;position&gt;" selector.</summary>
        private static long TicksOf(string selector)
        {
            if (string.IsNullOrEmpty(selector)) return -1;
            var dash = selector.IndexOf('-');
            var head = dash < 0 ? selector : selector.Substring(0, dash);
            return long.TryParse(head, out var ticks) ? ticks : -1;
        }

        private static string SubjectOf(ScheduleEntry entry) =>
            string.IsNullOrWhiteSpace(entry.Subject) ? "Vald tid" : entry.Subject;

        /// <summary>
        /// One planned server call. An edit produces two of these - the new interval to add and the
        /// old one to remove - sharing a single label so the log reads as one change, not two.
        /// </summary>
        private sealed class Change
        {
            public ScheduleEntry Old;
            public ScheduleEntry New;
            public string Label;
        }

        private static SaveOutcome Partial(SaveOutcome outcome, bool applied, string message)
        {
            outcome.Status = applied ? SaveStatus.PartiallyApplied : SaveStatus.Failed;
            outcome.Message = applied
                ? message + " Vissa ändringar hann sparas - läs om profilen för att se aktuellt läge."
                : message;
            return outcome;
        }

        private static string DescribePermissionProblem(PermissionState state)
        {
            switch (state)
            {
                case PermissionState.NotRegistered:
                    return "Pluginets behörigheter är inte registrerade på servern ännu. " +
                           "Administratören behöver starta Management Client en gång med pluginet installerat " +
                           "och sedan ge rollen rättigheten under Roller -> Tidsprofiler.";
                case PermissionState.Unavailable:
                    return "Behörigheten kunde inte kontrolleras mot servern. Försök igen, " +
                           "eller kontrollera anslutningen till Management Server.";
                default:
                    return "Du saknar behörighet att ändra tidsprofiler. Rättigheten ges per roll " +
                           "under Roller -> Tidsprofiler i Management Client.";
            }
        }

        private static bool LooksLikePermissionProblem(string message) =>
            PluginSecurity.LooksLikePermissionProblem(message);

        private static string Describe(ServerTask task) =>
            string.IsNullOrWhiteSpace(task?.ErrorText)
                ? (task?.ErrorCode ?? task?.State.ToString() ?? "okänt fel")
                : task.ErrorText;

        private TimeProfileFolder GetFolder(bool refresh)
        {
            var folder = new ManagementServer(ServerId).TimeProfileFolder;
            if (refresh) folder.ClearChildrenCache();
            return folder;
        }

        private TimeProfile FindProfile(Guid id, bool refresh)
        {
            var profile = GetFolder(refresh).TimeProfiles.FirstOrDefault(t => t.Guid == id);
            profile?.ClearChildrenCache();
            return profile;
        }

        /// <summary>A copy of the entry restricted to what the server can actually store.</summary>
        private static ScheduleEntry Normalized(ScheduleEntry entry)
        {
            var copy = entry.Clone();

            if (copy.Kind == ScheduleEntryKind.SingleOccurrence)
            {
                // Match what the server stores for an all-day booking, so the entry does not
                // compare as changed against its own saved form on every later save.
                if (copy.AllDayEvent && copy.OccurrenceStart.HasValue)
                {
                    copy.OccurrenceStart = copy.OccurrenceStart.Value.Date;
                    copy.OccurrenceEnd = copy.OccurrenceStart.Value.Date;
                }

                return copy;
            }

            if (copy.Duration > MaxDuration) copy.Duration = MaxDuration;
            if (copy.Duration < TimeSpan.Zero) copy.Duration = TimeSpan.Zero;

            // An end date before the start would silently disable the interval.
            if (copy.RangeEnd.HasValue && copy.RangeEnd.Value < copy.RangeStart)
                copy.RangeEnd = copy.RangeStart;

            return copy;
        }

        private static TimeSpan ParseTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return TimeSpan.Zero;
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : TimeSpan.Zero;
        }

        /// <summary>
        /// Formats as HH:mm:ss, clamped below 24 hours.
        ///
        /// The clamp is the important part: the server reads "24:00:00" and above as a number of
        /// days, so emitting one would quietly turn a full-day interval into a 24-day one. This is
        /// the single place every written duration passes through, which is why the guard lives
        /// here as well as in the UI.
        /// </summary>
        private static string FormatTime(TimeSpan value)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            if (value > MaxDuration) value = MaxDuration;
            return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
        }
    }
}
