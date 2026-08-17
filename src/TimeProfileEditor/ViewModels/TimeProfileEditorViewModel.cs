using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using TimeProfileEditor.Model;
using TimeProfileEditor.Security;
using TimeProfileEditor.Services;

namespace TimeProfileEditor.ViewModels
{
    internal enum StatusSeverity
    {
        None,
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// Which of the two read-only panels is over the editor, if either. One field rather than two
    /// flags, so opening one cannot leave the other showing underneath it.
    /// </summary>
    internal enum InfoPanel
    {
        None,

        /// <summary>How the plugin works, for whoever has to use it.</summary>
        Help,

        /// <summary>What the plugin is, for whoever has to support it.</summary>
        About
    }

    internal sealed class TimeProfileEditorViewModel : ObservableObject
    {
        /// <summary>Times snap to this, which is also the smallest block the week grid can show.</summary>
        public static readonly TimeSpan Snap = TimeSpan.FromMinutes(15);

        // Routed rather than direct: on Expert and Professional+ the Management Server refuses an
        // operator's write, and this is what sends it to the Event Server component instead. On
        // Corporate it is a straight pass-through. See RoutedTimeProfileRepository.
        private readonly RoutedTimeProfileRepository _repository = new RoutedTimeProfileRepository();

        /// <summary>Set when the save will have to take the long way round, or has nowhere to go.</summary>
        private string _routeNotice;
        private readonly Dispatcher _dispatcher;

        private string _filter = "";
        private TimeProfileInfo _selectedProfile;
        private ScheduleEntry _selectedEntry;
        private ProfileSchedule _loaded;
        private List<ScheduleEntry> _baseline = new List<ScheduleEntry>();
        private bool _isBusy;
        private int _busyDepth;
        private bool _isDirty;
        private string _statusMessage;
        private StatusSeverity _statusSeverity;
        private PermissionState _editPermission = PermissionState.Unavailable;
        private DateTime _calendarMonth = FirstOfMonth(DateTime.Today);
        private DateTime _weekStart = SwedishDates.MondayOf(DateTime.Today);
        private string _newTimeFrom = "08:00";
        private string _newTimeTo = "17:00";
        private bool _newTimeAllDay;
        private InfoPanel _openPanel;

        public TimeProfileEditorViewModel()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            SaveCommand = new RelayCommand(Save, () => CanEdit && IsDirty && !IsBusy);
            CancelCommand = new RelayCommand(Cancel, () => IsDirty && !IsBusy);
            ReloadCommand = new RelayCommand(() => ReloadEverything(), () => !IsBusy);
            AddEntryCommand = new RelayCommand(AddEntry, () => CanEdit && !IsBusy && SelectedProfile != null && !SelectedProfile.IsSunclock);
            DeleteEntryCommand = new RelayCommand(DeleteSelectedEntry, () => CanEdit && !IsBusy && SelectedEntry != null && SelectedEntry.IsEditable);

            AddDateCommand = new RelayCommand(AddDate, () => CanEdit && !IsBusy && SelectedProfile != null && !SelectedProfile.IsSunclock);
            CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics, () => !IsBusy);

            // No CanExecute on any of these. Reading what the plugin is and how it works has to
            // work when nothing else does - no profile open, no permission, no server answering -
            // because those are the moments someone reaches for them.
            ShowHelpCommand = new RelayCommand(() => Toggle(InfoPanel.Help));
            ShowAboutCommand = new RelayCommand(() => Toggle(InfoPanel.About));
            CloseInfoCommand = new RelayCommand(() => Toggle(InfoPanel.None));

            PreviousMonthCommand = new RelayCommand(() => CalendarMonth = CalendarMonth.AddMonths(-1));
            NextMonthCommand = new RelayCommand(() => CalendarMonth = CalendarMonth.AddMonths(1));
            TodayCommand = new RelayCommand(() =>
            {
                CalendarMonth = FirstOfMonth(DateTime.Today);
                WeekStart = DateTime.Today;
            });

            AddOnDatesCommand = new RelayCommand(AddOnSelectedDates, () => IsEditableProfileOpen && !IsBusy && SelectedDates.Any());
            AddWeeklyFromSelectionCommand = new RelayCommand(AddWeeklyFromSelection, () => IsEditableProfileOpen && !IsBusy && SelectedDates.Any());
            RemoveOnDatesCommand = new RelayCommand(RemoveOnSelectedDates, () => IsEditableProfileOpen && !IsBusy && SelectedDates.Any());

            // Only what the selection actually changes. Dragging across the month rebuilds this
            // collection on every mouse move, and each add is its own notification - putting the
            // full RaiseAll here would re-evaluate the permission banner a few hundred times a
            // second to answer a question the mouse cannot affect.
            SelectedDates.CollectionChanged += (s, e) =>
            {
                Raise(nameof(SelectionSummary));
                AddOnDatesCommand.RaiseCanExecuteChanged();
                AddWeeklyFromSelectionCommand.RaiseCanExecuteChanged();
                RemoveOnDatesCommand.RaiseCanExecuteChanged();

                // The week grid follows the calendar, so clicking a day shows that day's week in
                // full. Earliest rather than latest: a drag across the month clears and refills
                // this collection continuously, and following the last one added would make the
                // grid flicker between weeks under the pointer.
                if (SelectedDates.Count > 0) WeekStart = SelectedDates.Min();
            };

            WeeklyEntries.CollectionChanged += OnWeeklyCollectionChanged;
            DateEntries.CollectionChanged += OnWeeklyCollectionChanged;
        }

        public ObservableCollection<TimeProfileInfo> Profiles { get; } = new ObservableCollection<TimeProfileInfo>();

        /// <summary>The working copy of the weekly pattern. Nothing here reaches the server until Save runs.</summary>
        public ObservableCollection<ScheduleEntry> WeeklyEntries { get; } = new ObservableCollection<ScheduleEntry>();

        /// <summary>One-off bookings on specific dates - holidays, exceptions, single events.</summary>
        public ObservableCollection<ScheduleEntry> DateEntries { get; } = new ObservableCollection<ScheduleEntry>();

        /// <summary>Patterns the week grid cannot represent faithfully. Shown, never rewritten.</summary>
        public ObservableCollection<ScheduleEntry> ReadOnlyEntries { get; } = new ObservableCollection<ScheduleEntry>();

        private IEnumerable<ScheduleEntry> Editable => WeeklyEntries.Concat(DateEntries);

        /// <summary>
        /// Dates ticked in the month calendar. The calendar owns the ticking; everything that acts
        /// on them is a command here, so what a click means is decided in one place.
        /// </summary>
        public ObservableCollection<DateTime> SelectedDates { get; } = new ObservableCollection<DateTime>();

        public RelayCommand SaveCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ReloadCommand { get; }
        public RelayCommand AddEntryCommand { get; }
        public RelayCommand AddDateCommand { get; }
        public RelayCommand DeleteEntryCommand { get; }
        public RelayCommand CopyDiagnosticsCommand { get; }

        public RelayCommand ShowHelpCommand { get; }
        public RelayCommand ShowAboutCommand { get; }
        public RelayCommand CloseInfoCommand { get; }

        public RelayCommand PreviousMonthCommand { get; }
        public RelayCommand NextMonthCommand { get; }
        public RelayCommand TodayCommand { get; }
        public RelayCommand AddOnDatesCommand { get; }
        public RelayCommand AddWeeklyFromSelectionCommand { get; }
        public RelayCommand RemoveOnDatesCommand { get; }

        /// <summary>Raised when the grid needs to redraw because the entry set changed wholesale.</summary>
        public event EventHandler ScheduleReplaced;

        /// <summary>Asks the view to confirm discarding unsaved edits. Returns true to continue.</summary>
        public Func<string, bool> ConfirmDiscard { get; set; }

        /// <summary>
        /// Asks the view to confirm an edit that reaches further than the operator asked for.
        /// Separate from <see cref="ConfirmDiscard"/> because it is a different question with a
        /// different answer if it goes unwired: discarding defaults to allowed, this defaults to
        /// refused, so an unattached callback leaves the weekly pattern alone rather than rewriting it.
        /// </summary>
        public Func<string, bool> ConfirmAction { get; set; }

        public string Filter
        {
            get => _filter;
            set
            {
                if (Set(ref _filter, value)) Raise(nameof(VisibleProfiles));
            }
        }

        public IEnumerable<TimeProfileInfo> VisibleProfiles =>
            string.IsNullOrWhiteSpace(Filter)
                ? Profiles
                : Profiles.Where(p => p.Name != null &&
                                      p.Name.IndexOf(Filter, StringComparison.CurrentCultureIgnoreCase) >= 0);

        public TimeProfileInfo SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (ReferenceEquals(_selectedProfile, value)) return;

                if (IsDirty && _selectedProfile != null && ConfirmDiscard != null &&
                    !ConfirmDiscard($"Du har osparade ändringar i '{_selectedProfile.Name}'. Vill du kasta dem?"))
                {
                    // Put the list selection back without re-entering this setter.
                    _dispatcher.BeginInvoke(new Action(() => Raise(nameof(SelectedProfile))),
                        DispatcherPriority.Background);
                    return;
                }

                Set(ref _selectedProfile, value);
                RaiseAll();
                LoadSchedule(value);
            }
        }

        public ScheduleEntry SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (Set(ref _selectedEntry, value)) RaiseAll();
            }
        }

        /// <summary>
        /// A count rather than a flag: a completed operation may start another one from its own
        /// callback (saving reloads the profile), and a plain bool would clear the overlay - and
        /// re-enable Save - while that follow-up was still in flight.
        /// </summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value)) RaiseAll();
            }
        }

        public bool IsDirty
        {
            get => _isDirty;
            private set
            {
                if (Set(ref _isDirty, value)) RaiseAll();
            }
        }

        public bool CanEdit => _editPermission == PermissionState.Granted;

        /// <summary>True when there is a profile open that this plugin is willing to edit.</summary>
        public bool IsEditableProfileOpen =>
            SelectedProfile != null && !SelectedProfile.IsSunclock && CanEdit;

        public string PermissionBanner
        {
            get
            {
                // Shown even though editing is "granted", and shown first. This build grants it by
                // refusing to ask, and the person in front of it has to know that before they read
                // anything else on the screen as a statement about their rights.
                if (SystemEdition.Configured == EditionMode.Measurement)
                    return "MÄTLÄGE: den här versionen kontrollerar ingen behörighet i pluginet. " +
                           "Den finns för att mäta vad servern svarar och ska inte användas i drift. " +
                           "Sparandet prövas fortfarande av Management Server, så ett försök som " +
                           "nekas nekas av servern.";

                switch (_editPermission)
                {
                    // Permitted by the plugin, but the write may still have nowhere to land: on
                    // Expert and Professional+ the Management Server takes configuration only from
                    // an administrator, and the Event Server component is what carries it instead.
                    // Said here rather than after a failed save, because by then the operator has
                    // done the work twice.
                    case PermissionState.Granted:
                        return _routeNotice;

                    case PermissionState.NotRegistered:
                        return "Pluginets behörigheter är inte registrerade på servern ännu. Du kan läsa " +
                               "tidsprofiler men inte spara. Administratören startar Management Client en gång " +
                               "med pluginet installerat och ger sedan rollen rättigheten under Roller → Tidsprofiler.";
                    case PermissionState.Unavailable:
                        // Include the underlying failure. A bare "could not be checked" leaves an
                        // administrator with nowhere to go, and this state is the one that most
                        // often means something about the environment rather than the user.
                        var detail = PluginSecurity.LastError;
                        return "Behörigheten kunde inte kontrolleras mot servern. Du kan läsa tidsprofiler men inte spara." +
                               (string.IsNullOrWhiteSpace(detail) ? "" : Environment.NewLine + "Orsak: " + detail);
                    default:
                        // The right can be granted on every product tier, so this is now a sentence
                        // an administrator can act on rather than a product limitation to explain
                        // away. Where it says so is the same place on Corporate, Expert and
                        // Professional+ alike.
                        return "Du har läsbehörighet till tidsprofiler men saknar behörighet att ändra dem. " +
                               "Administratören ger rollen rättigheten under Roller → Tidsprofiler.";
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => Set(ref _statusMessage, value);
        }

        public StatusSeverity StatusSeverity
        {
            get => _statusSeverity;
            private set => Set(ref _statusSeverity, value);
        }

        public string ProfileSubtitle
        {
            get
            {
                if (SelectedProfile == null) return null;
                if (SelectedProfile.IsSunclock)
                    return "Sunclock-profil – styrs av soluppgång och solnedgång och redigeras i Management Client.";
                return string.IsNullOrWhiteSpace(SelectedProfile.Description)
                    ? "Kalenderprofil"
                    : SelectedProfile.Description;
            }
        }

        // ---- Help and information ------------------------------------------------------------
        //
        // Both panels are read-only and neither touches the schedule, so they sit over the editor
        // rather than replacing it: whatever was on screen is still there, unchanged, when the
        // panel closes. Nothing is loaded for them - the texts are compiled in and the facts come
        // out of the assembly - so opening one cannot fail or hang.

        public IReadOnlyList<HelpTopic> HelpTopics => HelpText.All;

        public IReadOnlyList<Fact> AboutFacts => PluginInfo.Facts;

        public string AboutDescription => PluginInfo.Description;

        public bool IsInfoOpen => _openPanel != InfoPanel.None;

        public bool IsHelpOpen => _openPanel == InfoPanel.Help;

        public bool IsAboutOpen => _openPanel == InfoPanel.About;

        public string InfoTitle =>
            _openPanel == InfoPanel.About ? "Om " + PluginInfo.Name : "Så fungerar " + PluginInfo.Name;

        /// <summary>
        /// Opens the panel, or closes it when it is the one already open. Pressing the same button
        /// twice is how a person closes something they opened by mistake, and there is no reason
        /// for that to be the one gesture that does nothing.
        /// </summary>
        private void Toggle(InfoPanel panel)
        {
            _openPanel = _openPanel == panel ? InfoPanel.None : panel;

            Raise(nameof(IsInfoOpen));
            Raise(nameof(IsHelpOpen));
            Raise(nameof(IsAboutOpen));
            Raise(nameof(InfoTitle));
        }

        // ---- Month calendar ------------------------------------------------------------------
        //
        // The week grid edits a pattern; this side shows what that pattern lands on. Both are
        // needed: a weekly interval whose validity period ran out last month looks perfectly
        // healthy in the week grid, and only a calendar of real dates shows that it stopped
        // applying. It is also the shortest way to say "these days" - clicking them.

        public DateTime CalendarMonth
        {
            get => _calendarMonth;
            set
            {
                if (Set(ref _calendarMonth, FirstOfMonth(value))) Raise(nameof(CalendarMonthLabel));
            }
        }

        public string CalendarMonthLabel => SwedishDates.MonthAndYear(CalendarMonth);

        /// <summary>
        /// Which week the week grid shows. Driven by the calendar selection rather than by its own
        /// navigation: the two panels answer different questions about the same profile, and giving
        /// each a separate idea of "when" is how they end up showing different weeks side by side.
        /// </summary>
        public DateTime WeekStart
        {
            get => _weekStart;
            private set => Set(ref _weekStart, SwedishDates.MondayOf(value));
        }

        /// <summary>Start of the interval the calendar buttons add. Parsed when used, not while typed.</summary>
        public string NewTimeFrom
        {
            get => _newTimeFrom;
            set => Set(ref _newTimeFrom, value);
        }

        public string NewTimeTo
        {
            get => _newTimeTo;
            set => Set(ref _newTimeTo, value);
        }

        public bool NewTimeAllDay
        {
            get => _newTimeAllDay;
            set => Set(ref _newTimeAllDay, value);
        }

        public string SelectionSummary
        {
            get
            {
                var dates = SelectedDates.OrderBy(d => d).ToList();
                if (!dates.Any())
                    return "Inga datum valda. Klicka i kalendern, dra för flera dagar, klicka på ett " +
                           "veckonummer för hela veckan eller på ett dagnamn för alla sådana dagar i månaden.";

                if (dates.Count == 1)
                {
                    var day = Coverage.For(dates[0], Editable);
                    return SwedishDates.LongDate(dates[0]) + " – " +
                           (day.IsCovered ? Hours(day.Total) + " tid" : "ingen tid");
                }

                var covered = dates.Count(d => Coverage.For(d, Editable).IsCovered);
                return $"{dates.Count} datum valda, {SwedishDates.ShortDate(dates.First())} – " +
                       $"{SwedishDates.ShortDate(dates.Last())}. {covered} av {dates.Count} har tid.";
            }
        }

        /// <summary>
        /// Said out loud when the profile holds patterns the calendar cannot place on dates, so an
        /// empty day reads as "nothing this panel can draw" rather than "nothing at all".
        /// </summary>
        public string CalendarNote =>
            ReadOnlyEntries.Count == 0
                ? null
                : $"Profilen har {ReadOnlyEntries.Count} tid(er) med ett mönster som inte går att " +
                  "placera på datum här. De ritas inte i kalendern, ligger kvar orörda och listas " +
                  "under \"Övriga tider i profilen\".";

        /// <summary>Adds a one-off booking on every selected date.</summary>
        private void AddOnSelectedDates()
        {
            if (!IsEditableProfileOpen || !SelectedDates.Any()) return;
            if (!ReadNewTimes(out var start, out var duration)) return;

            var added = 0;
            var duplicates = 0;
            ScheduleEntry last = null;

            foreach (var date in SelectedDates.OrderBy(d => d).ToList())
            {
                var entry = new ScheduleEntry
                {
                    Kind = ScheduleEntryKind.SingleOccurrence,
                    AllDayEvent = NewTimeAllDay,
                    OccurrenceStart = NewTimeAllDay ? date.Date : date.Date + start,
                    OccurrenceEnd = NewTimeAllDay ? date.Date : date.Date + start + duration,
                    Subject = "Vald tid"
                };

                // Pressing the button twice must not quietly double every booking.
                if (DateEntries.Any(e => e.HasSameScheduleAs(entry)))
                {
                    duplicates++;
                    continue;
                }

                DateEntries.Add(entry);
                last = entry;
                added++;
            }

            if (added == 1) SelectedEntry = last;

            var when = NewTimeAllDay
                ? "heldag"
                : $"{TimeText.Format(start)}–{TimeText.Format(start + duration)}";

            SetStatus(
                added == 0
                    ? "De valda datumen hade redan den tiden. Inget lades till."
                    : $"{added} datum tillagda, {when}." +
                      (duplicates > 0 ? $" {duplicates} hade tiden redan." : "") +
                      " Tryck Spara för att skriva dem till servern.",
                StatusSeverity.Info);
        }

        /// <summary>
        /// Turns the selection into one weekly pattern: the weekdays it touches, valid from the
        /// first selected date to the last.
        ///
        /// This is the honest reading of "every Monday this term" - one pattern rather than sixteen
        /// separate bookings. It does mean the pattern also covers dates between the first and last
        /// that were not selected, which is why the status line says so rather than leaving the
        /// operator to notice it in the calendar afterwards.
        /// </summary>
        private void AddWeeklyFromSelection()
        {
            if (!IsEditableProfileOpen || !SelectedDates.Any()) return;
            if (!ReadNewTimes(out var start, out var duration)) return;

            var dates = SelectedDates.OrderBy(d => d).ToList();
            var days = dates.Aggregate(DayFlags.None, (flags, date) => flags | date.DayOfWeek.ToFlag());

            var entry = new ScheduleEntry
            {
                Kind = ScheduleEntryKind.Weekly,
                Days = days,
                Start = start,
                Duration = duration,
                RangeStart = dates.First().Date,
                RangeEnd = dates.Last().Date,
                Subject = "Vald tid"
            };

            WeeklyEntries.Add(entry);
            SelectedEntry = entry;

            SetStatus(
                $"Veckomönster tillagt: {entry.Describe()}. Det gäller varje vecka i perioden, " +
                "alltså även datum mellan det första och sista du valde. Ska det gälla tills vidare, " +
                "kryssa i \"Tills vidare\" i panelen till höger." +
                (NewTimeAllDay
                    ? " Heldag skrivs som 00:00–23:59, eftersom servern läser exakt ett dygn som ett antal dygn."
                    : ""),
                StatusSeverity.Info);
        }

        /// <summary>
        /// Clears time on the selected dates.
        ///
        /// One-off bookings simply go. A weekly pattern cannot be switched off for a single date -
        /// a time profile is the union of its appointments and has no concept of an exception - so
        /// the only way to clear a weekday is to take it out of the pattern, and that empties it
        /// every week the pattern runs. Nothing about "remove time on these dates" implies that, so
        /// it is asked before it is done and refused if there is nobody to ask.
        /// </summary>
        private void RemoveOnSelectedDates()
        {
            if (!IsEditableProfileOpen || !SelectedDates.Any()) return;

            var dates = new HashSet<DateTime>(SelectedDates.Select(d => d.Date));

            var bookings = DateEntries
                .Where(e => e.OccurrenceStart.HasValue && dates.Contains(e.OccurrenceStart.Value.Date))
                .ToList();
            foreach (var booking in bookings) DateEntries.Remove(booking);

            var patterns = WeeklyEntries.Where(e => dates.Any(d => Coverage.AppliesOn(e, d))).ToList();
            var weekdays = DayFlagsExtensions.WeekOrder
                .Where(day => dates.Any(d => d.DayOfWeek == day && patterns.Any(p => Coverage.AppliesOn(p, d))))
                .Aggregate(DayFlags.None, (flags, day) => flags | day.ToFlag());

            var clearedWeekdays = false;
            if (patterns.Any() && weekdays != DayFlags.None)
            {
                var question =
                    $"De valda datumen täcks också av ett veckomönster: {weekdays.Describe().ToLowerInvariant()}." +
                    Environment.NewLine + Environment.NewLine +
                    "Ett veckomönster går inte att stänga av för enstaka datum. Tas veckodagarna bort " +
                    "försvinner tiden varje vecka så länge mönstret gäller, inte bara på de datum du valt." +
                    Environment.NewLine + Environment.NewLine +
                    "Vill du ta bort dem ur mönstret?";

                if (ConfirmAction != null && ConfirmAction(question))
                {
                    foreach (var pattern in patterns)
                    {
                        var left = pattern.Days & ~weekdays;

                        // A pattern on no days would still be stored and would still apply to
                        // nothing, which reads as a bug. It goes instead.
                        if (left == DayFlags.None) WeeklyEntries.Remove(pattern);
                        else pattern.Days = left;
                    }

                    clearedWeekdays = true;
                }
            }

            if (bookings.Count == 0 && !clearedWeekdays)
            {
                SetStatus(
                    patterns.Any()
                        ? "Inga enstaka datum fanns på de valda dagarna. Veckomönstret lämnades orört."
                        : "De valda datumen hade ingen tid.",
                    StatusSeverity.Info);
                return;
            }

            SetStatus(
                (bookings.Count > 0 ? $"{bookings.Count} enstaka datum borttagna. " : "") +
                (clearedWeekdays ? $"{weekdays.Describe().ToLowerInvariant()} borttagna ur veckomönstret. " : "") +
                "Tryck Spara för att skriva ändringen till servern.",
                StatusSeverity.Info);
        }

        /// <summary>Opens whatever the calendar was double-clicked on, if there is anything there.</summary>
        public void SelectEntryOn(DateTime date)
        {
            var booking = DateEntries.FirstOrDefault(
                e => e.OccurrenceStart.HasValue && e.OccurrenceStart.Value.Date == date.Date);

            SelectedEntry = booking ?? WeeklyEntries.FirstOrDefault(e => Coverage.AppliesOn(e, date));
        }

        /// <summary>
        /// Reads the two time boxes. False means they could not be read, and the operator has been
        /// told why - no caller may carry on with a guess.
        /// </summary>
        private bool ReadNewTimes(out TimeSpan start, out TimeSpan duration)
        {
            if (NewTimeAllDay)
            {
                // A weekly interval of exactly 24 hours is read by the server as a number of days,
                // so a whole day is 00:00-23:59. See TimeProfileRepository.MaxDuration.
                start = TimeSpan.Zero;
                duration = TimeProfileRepository.MaxDuration;
                return true;
            }

            start = TimeSpan.Zero;
            duration = TimeSpan.Zero;

            var from = TimeText.Parse(NewTimeFrom);
            var to = TimeText.Parse(NewTimeTo);
            if (from == null || to == null)
            {
                SetStatus("Tiden gick inte att tolka. Skriv den som 08:00.", StatusSeverity.Warning);
                return false;
            }

            start = from.Value;
            var end = to.Value;

            // "22:00 to 06:00" is a night shift, not a mistake.
            if (end <= start) end += TimeSpan.FromHours(24);

            duration = end - start;
            if (duration < Snap) duration = Snap;
            if (duration > TimeProfileRepository.MaxDuration) duration = TimeProfileRepository.MaxDuration;
            return true;
        }

        private static DateTime FirstOfMonth(DateTime date) => new DateTime(date.Year, date.Month, 1);

        /// <summary>"8,5 tim" - with a Swedish decimal comma whatever the machine's locale is.</summary>
        private static string Hours(TimeSpan value) =>
            value.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                .Replace('.', ',') + " tim";

        public void Initialize()
        {
            _editPermission = PluginSecurity.CanEdit();
            RaiseAll();
            ReloadEverything();
        }

        /// <summary>
        /// Puts the full picture on the clipboard. The banner only has room for a sentence, and the
        /// cases that need explaining happen on servers nobody can reach from here.
        /// </summary>
        private void CopyDiagnostics()
        {
            RunInBackground(
                () => Security.Diagnostics.Report(),
                report =>
                {
                    try
                    {
                        System.Windows.Clipboard.SetText(report);
                        SetStatus("Diagnostiken är kopierad. Klistra in den där den behövs.", StatusSeverity.Info);
                    }
                    catch (Exception ex)
                    {
                        // A locked clipboard must not swallow the report - the log still gets it.
                        ChangeLog.Error("Kunde inte kopiera diagnostiken till urklipp", ex);
                        SetStatus("Urklipp kunde inte skrivas. Diagnostiken finns i MIP-loggen.",
                            StatusSeverity.Warning);
                    }

                    ChangeLog.Info("Diagnostik:" + Environment.NewLine + report);
                },
                "Kunde inte ta fram diagnostiken");
        }

        private void ReloadEverything()
        {
            // Re-ask for permissions too, so a check that failed because the client was still
            // logging in can be retried from the UI instead of by restarting Smart Client.
            PluginSecurity.Reset();
            ServerComponentChannel.Forget();
            _editPermission = PluginSecurity.CanEdit();
            _routeNotice = null;
            RaiseAll();

            var keepId = SelectedProfile?.Id;
            RunInBackground(
                () => _repository.LoadProfiles(),
                profiles =>
                {
                    Profiles.Clear();
                    foreach (var p in profiles) Profiles.Add(p);
                    Raise(nameof(VisibleProfiles));

                    var restored = keepId.HasValue ? Profiles.FirstOrDefault(p => p.Id == keepId.Value) : null;
                    _selectedProfile = null;
                    SelectedProfile = restored ?? Profiles.FirstOrDefault();

                    if (!Profiles.Any())
                        SetStatus("Inga tidsprofiler hittades, eller så saknar du behörighet att läsa dem.",
                            StatusSeverity.Info);

                    CheckRoute();
                },
                "Kunde inte hämta tidsprofiler");
        }

        /// <summary>
        /// Works out, in the background, whether this user's save will have somewhere to go - and
        /// says so before they have written anything.
        ///
        /// Only asked when it can matter: the user may edit as far as the plugin is concerned, but
        /// the Management Server will not take the write from them. Finding out at that point costs
        /// an operator their work, so the answer is fetched while the list loads instead.
        ///
        /// In the background because it is a round trip to the Event Server and back, with a
        /// ten-second timeout when nobody is listening - which is exactly the case where the answer
        /// matters, and exactly the case where blocking the UI for it would be worst.
        /// </summary>
        private void CheckRoute()
        {
            if (_editPermission != PermissionState.Granted) return;
            if (SystemEdition.ConfigurationAccess() != ConfigAccess.Denied) return;

            // Deliberately not RunInBackground: that marks the view model busy, and this can take
            // ten seconds when the answer is "nobody is listening". The operator is not waiting for
            // this and must not be stopped from working while it runs.
            Task.Run(() =>
            {
                string notice;
                try
                {
                    notice = _repository.DescribeRoute();
                }
                catch (Exception ex)
                {
                    ChangeLog.Error("Kunde inte kontrollera vägen till serverkomponenten", ex);
                    return;
                }

                _dispatcher.BeginInvoke(new Action(() =>
                {
                    _routeNotice = notice;
                    Raise(nameof(PermissionBanner));
                }));
            });
        }

        private void LoadSchedule(TimeProfileInfo profile)
        {
            ClearSchedule();
            if (profile == null) return;

            RunInBackground(
                () => _repository.LoadSchedule(profile.Id),
                schedule =>
                {
                    if (schedule == null)
                    {
                        SetStatus("Tidsprofilen kunde inte läsas. Den kan ha tagits bort.", StatusSeverity.Warning);
                        return;
                    }

                    _loaded = schedule;
                    ApplySchedule(schedule.Entries);
                    SetStatus(null, StatusSeverity.None);
                },
                "Kunde inte läsa tidsprofilen");
        }

        private void ApplySchedule(IEnumerable<ScheduleEntry> entries)
        {
            DetachEntryHandlers();
            WeeklyEntries.CollectionChanged -= OnWeeklyCollectionChanged;
            DateEntries.CollectionChanged -= OnWeeklyCollectionChanged;
            WeeklyEntries.Clear();
            DateEntries.Clear();
            ReadOnlyEntries.Clear();

            foreach (var entry in entries.OrderBy(e => e.OccurrenceStart ?? DateTime.MaxValue))
            {
                switch (entry.Kind)
                {
                    case ScheduleEntryKind.Weekly:
                        entry.PropertyChanged += OnEntryChanged;
                        WeeklyEntries.Add(entry);
                        break;
                    case ScheduleEntryKind.SingleOccurrence:
                        entry.PropertyChanged += OnEntryChanged;
                        DateEntries.Add(entry);
                        break;
                    default:
                        ReadOnlyEntries.Add(entry);
                        break;
                }
            }

            WeeklyEntries.CollectionChanged += OnWeeklyCollectionChanged;
            DateEntries.CollectionChanged += OnWeeklyCollectionChanged;
            _baseline = Editable.Select(e => e.Clone()).ToList();
            IsDirty = false;
            SelectedEntry = null;
            ScheduleReplaced?.Invoke(this, EventArgs.Empty);
            RaiseAll();
        }

        private void ClearSchedule()
        {
            _loaded = null;
            DetachEntryHandlers();
            WeeklyEntries.CollectionChanged -= OnWeeklyCollectionChanged;
            DateEntries.CollectionChanged -= OnWeeklyCollectionChanged;
            WeeklyEntries.Clear();
            DateEntries.Clear();
            ReadOnlyEntries.Clear();
            WeeklyEntries.CollectionChanged += OnWeeklyCollectionChanged;
            DateEntries.CollectionChanged += OnWeeklyCollectionChanged;
            _baseline = new List<ScheduleEntry>();
            IsDirty = false;
            SelectedEntry = null;

            // The ticked dates belong to the profile that was open. Carrying them into the next one
            // would leave the buttons pointed at days the operator picked while looking at
            // something else.
            SelectedDates.Clear();
            ScheduleReplaced?.Invoke(this, EventArgs.Empty);
        }

        private void DetachEntryHandlers()
        {
            foreach (var entry in Editable) entry.PropertyChanged -= OnEntryChanged;
        }

        private void OnWeeklyCollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (ScheduleEntry item in e.NewItems) item.PropertyChanged += OnEntryChanged;
            if (e.OldItems != null)
                foreach (ScheduleEntry item in e.OldItems) item.PropertyChanged -= OnEntryChanged;

            MarkDirty();
        }

        private void OnEntryChanged(object sender, PropertyChangedEventArgs e) => MarkDirty();

        private void MarkDirty()
        {
            IsDirty = !SameAsBaseline();

            // The calendar counts how much of the selection is covered, and an edit in the week
            // grid changes that answer without touching the selection itself.
            Raise(nameof(SelectionSummary));
            ScheduleReplaced?.Invoke(this, EventArgs.Empty);
        }

        private bool SameAsBaseline()
        {
            var current = Editable.ToList();
            if (current.Count != _baseline.Count) return false;

            var remaining = _baseline.ToList();
            foreach (var entry in current)
            {
                var match = remaining.FirstOrDefault(b => b.Key == entry.Key && b.HasSameScheduleAs(entry));
                if (match == null) return false;
                remaining.Remove(match);
            }

            return true;
        }

        public void AddEntry()
        {
            if (!IsEditableProfileOpen) return;

            var entry = new ScheduleEntry
            {
                Kind = ScheduleEntryKind.Weekly,
                Days = DayFlags.Weekdays,
                Start = TimeSpan.FromHours(8),
                Duration = TimeSpan.FromHours(9),
                Subject = "Vald tid"
            };

            WeeklyEntries.Add(entry);
            SelectedEntry = entry;
        }

        /// <summary>Adds a one-off booking, defaulting to tomorrow so it does not land in the past.</summary>
        public void AddDate()
        {
            if (!IsEditableProfileOpen) return;

            var day = DateTime.Today.AddDays(1);
            var entry = new ScheduleEntry
            {
                Kind = ScheduleEntryKind.SingleOccurrence,
                OccurrenceStart = day.AddHours(8),
                OccurrenceEnd = day.AddHours(17),
                Subject = "Enstaka datum"
            };

            DateEntries.Add(entry);
            SelectedEntry = entry;
        }

        public void DeleteSelectedEntry()
        {
            var entry = SelectedEntry;
            if (entry == null || !entry.IsEditable) return;

            if (!WeeklyEntries.Remove(entry)) DateEntries.Remove(entry);
            SelectedEntry = null;
        }

        private void Cancel()
        {
            if (_loaded == null) return;
            if (ConfirmDiscard != null && !ConfirmDiscard("Vill du kasta dina ändringar och läsa om tidsprofilen?"))
                return;

            LoadSchedule(SelectedProfile);
            SetStatus("Ändringarna kastades.", StatusSeverity.Info);
        }

        private void Save()
        {
            if (_loaded == null || SelectedProfile == null) return;

            var profileName = SelectedProfile.Name;
            var profileId = SelectedProfile.Id;
            var desired = Editable.Select(e => e.Clone()).ToList();
            var baseline = _baseline.Select(e => e.Clone()).ToList();
            var expected = _loaded.LastModified;

            RunInBackground(
                () => _repository.Save(profileId, desired, baseline, expected),
                outcome =>
                {
                    switch (outcome.Status)
                    {
                        case SaveStatus.Success:
                            ChangeLog.Saved(profileName, outcome.AppliedChanges);
                            SetStatus($"Ändringarna har sparats. ({outcome.AppliedChanges.Count} ändring(ar))",
                                StatusSeverity.Success);
                            LoadSchedule(SelectedProfile);
                            break;

                        case SaveStatus.NothingToDo:
                            SetStatus(outcome.Message, StatusSeverity.Info);
                            IsDirty = false;
                            break;

                        case SaveStatus.PermissionDenied:
                            ChangeLog.Refused(profileName, outcome.Message);
                            SetStatus(outcome.Message, StatusSeverity.Error);
                            // The server is the authority; if it says no, stop offering Save.
                            _editPermission = PluginSecurity.CanEdit();
                            RaiseAll();
                            break;

                        case SaveStatus.Conflict:
                            ChangeLog.Failed(profileName, outcome.Message);
                            SetStatus(outcome.Message, StatusSeverity.Warning);
                            break;

                        case SaveStatus.PartiallyApplied:
                            ChangeLog.Failed(profileName, outcome.Message);
                            SetStatus(outcome.Message, StatusSeverity.Error);
                            LoadSchedule(SelectedProfile);
                            break;

                        default:
                            ChangeLog.Failed(profileName, outcome.Message);
                            SetStatus("Det gick inte att spara ändringarna. " + outcome.Message, StatusSeverity.Error);
                            break;
                    }
                },
                "Det gick inte att spara ändringarna");
        }

        private void SetStatus(string message, StatusSeverity severity)
        {
            StatusMessage = message;
            StatusSeverity = severity;
        }

        /// <summary>
        /// Configuration API calls are blocking round trips to the Management Server; running them
        /// on the Smart Client UI thread would freeze the whole application, not just this tab.
        /// </summary>
        private void RunInBackground<T>(Func<T> work, Action<T> onSuccess, string failureText)
        {
            EnterBusy();
            Task.Run(work).ContinueWith(task =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (task.IsFaulted)
                        {
                            var message = task.Exception?.GetBaseException().Message ?? "okänt fel";
                            ChangeLog.Error(failureText, task.Exception?.GetBaseException());
                            SetStatus($"{failureText}: {message}", StatusSeverity.Error);
                            return;
                        }

                        onSuccess(task.Result);
                    }
                    finally
                    {
                        LeaveBusy();
                    }
                }));
            });
        }

        private void EnterBusy()
        {
            _busyDepth++;
            IsBusy = true;
        }

        private void LeaveBusy()
        {
            if (_busyDepth > 0) _busyDepth--;
            IsBusy = _busyDepth > 0;
        }

        private void RaiseAll()
        {
            Raise(nameof(CanEdit));
            Raise(nameof(IsEditableProfileOpen));
            Raise(nameof(PermissionBanner));
            Raise(nameof(ProfileSubtitle));
            Raise(nameof(CalendarNote));
            SaveCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ReloadCommand.RaiseCanExecuteChanged();
            AddEntryCommand.RaiseCanExecuteChanged();
            AddDateCommand.RaiseCanExecuteChanged();
            DeleteEntryCommand.RaiseCanExecuteChanged();
            AddOnDatesCommand.RaiseCanExecuteChanged();
            AddWeeklyFromSelectionCommand.RaiseCanExecuteChanged();
            RemoveOnDatesCommand.RaiseCanExecuteChanged();
        }
    }
}
