using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace TimeProfileEditor.Model
{
    /// <summary>
    /// Days of the week as XProtect stores them in RecurrencePatternDaysOfWeek.
    ///
    /// Verified against the Management Server, not assumed: adding one appointment per bit
    /// and reading back the server's own RecurrenceDescription gives Sunday for 1, Monday
    /// for 2 ... Saturday for 64. That is exactly 1 &lt;&lt; (int)DayOfWeek.
    /// (The API's default value of 31 is Sun-Thu, which is *not* "weekdays" - do not copy it.)
    /// </summary>
    [Flags]
    internal enum DayFlags
    {
        None = 0,
        Sunday = 1 << 0,
        Monday = 1 << 1,
        Tuesday = 1 << 2,
        Wednesday = 1 << 3,
        Thursday = 1 << 4,
        Friday = 1 << 5,
        Saturday = 1 << 6,

        Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
        Weekend = Saturday | Sunday,
        All = Weekdays | Weekend
    }

    internal static class DayFlagsExtensions
    {
        /// <summary>Monday-first order, matching how Swedish calendars are read.</summary>
        public static readonly DayOfWeek[] WeekOrder =
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
        };

        private static readonly string[] ShortNames = { "Mån", "Tis", "Ons", "Tor", "Fre", "Lör", "Sön" };
        private static readonly string[] LongNames =
        {
            "måndag", "tisdag", "onsdag", "torsdag", "fredag", "lördag", "söndag"
        };

        public static DayFlags ToFlag(this DayOfWeek day) => (DayFlags)(1 << (int)day);

        public static bool Has(this DayFlags flags, DayOfWeek day) => (flags & day.ToFlag()) != 0;

        public static string ShortName(DayOfWeek day) => ShortNames[Array.IndexOf(WeekOrder, day)];

        /// <summary>"onsdag" - lower case, as it is written in Swedish mid-sentence.</summary>
        public static string LongName(DayOfWeek day) => LongNames[Array.IndexOf(WeekOrder, day)];

        public static IEnumerable<DayOfWeek> Days(this DayFlags flags) => WeekOrder.Where(d => flags.Has(d));

        /// <summary>"Vardagar", "Alla dagar", "Helg" or an explicit list - whichever reads best.</summary>
        public static string Describe(this DayFlags flags)
        {
            if (flags == DayFlags.None) return "Inga dagar";
            if (flags == DayFlags.All) return "Alla dagar";
            if (flags == DayFlags.Weekdays) return "Vardagar";
            if (flags == DayFlags.Weekend) return "Helg";

            var names = flags.Days().Select(d => LongNames[Array.IndexOf(WeekOrder, d)]).ToList();
            return names.Count == 1
                ? char.ToUpper(names[0][0]) + names[0].Substring(1)
                : string.Join(", ", names.Take(names.Count - 1)) + " och " + names.Last();
        }
    }

    internal enum ScheduleEntryKind
    {
        /// <summary>A weekly recurring appointment - the only kind this plugin edits.</summary>
        Weekly,

        /// <summary>Daily/monthly/yearly recurrence. Shown so the week is honest, never rewritten.</summary>
        OtherRecurring,

        /// <summary>A one-off appointment on a specific date.</summary>
        SingleOccurrence
    }

    /// <summary>
    /// One time interval inside a time profile. Equality is by content, not identity, so the
    /// repository can tell an untouched entry from an edited one and leave the former alone.
    /// </summary>
    internal sealed class ScheduleEntry : INotifyPropertyChanged
    {
        private DayFlags _days;
        private TimeSpan _start;
        private TimeSpan _duration;
        private string _subject = "Vald tid";
        private DateTime _rangeStart = DateTime.Today;
        private DateTime? _rangeEnd;
        private DateTime? _occurrenceStart;
        private DateTime? _occurrenceEnd;
        private bool _allDayEvent;

        /// <summary>
        /// Client-side identity, stable for as long as the profile stays open.
        ///
        /// It exists because the server's own AppointmentRootId is not an identity at all: the
        /// Management Server hands out a fresh one on every read, so the same unchanged appointment
        /// comes back under a different id each time it is fetched. Anything that has to survive
        /// from "profile loaded" to "user pressed Save" is keyed on this instead, and the server id
        /// is only ever used within the single read that produced it.
        /// </summary>
        public Guid Key { get; private set; } = Guid.NewGuid();

        /// <summary>Per-read server handle. Valid only for the read it came from - never store it.</summary>
        public string AppointmentRootId { get; set; }

        public ScheduleEntryKind Kind { get; set; } = ScheduleEntryKind.Weekly;

        public DayFlags Days
        {
            get => _days;
            set => Set(ref _days, value);
        }

        public TimeSpan Start
        {
            get => _start;
            set => Set(ref _start, value);
        }

        public TimeSpan Duration
        {
            get => _duration;
            set => Set(ref _duration, value);
        }

        public string Subject
        {
            get => _subject;
            set => Set(ref _subject, value);
        }

        /// <summary>The server's own rendering, used verbatim for entries we refuse to edit.</summary>
        public string ServerDescription { get; set; }

        /// <summary>
        /// First date the weekly pattern applies. The server always stores one, so there is no
        /// "no start" - an unrestricted profile simply starts on the day it was created.
        /// </summary>
        public DateTime RangeStart
        {
            get => _rangeStart;
            set => Set(ref _rangeStart, value.Date);
        }

        /// <summary>
        /// Last date the weekly pattern applies, or null for "tills vidare". Maps onto the server's
        /// RecurrenceRangeLimit: null means NoLimit, a value means LimitByDate.
        /// </summary>
        public DateTime? RangeEnd
        {
            get => _rangeEnd;
            set => Set(ref _rangeEnd, value?.Date);
        }

        /// <summary>Start of a one-off booking. Set for <see cref="ScheduleEntryKind.SingleOccurrence"/>.</summary>
        public DateTime? OccurrenceStart
        {
            get => _occurrenceStart;
            set => Set(ref _occurrenceStart, value);
        }

        public DateTime? OccurrenceEnd
        {
            get => _occurrenceEnd;
            set => Set(ref _occurrenceEnd, value);
        }

        /// <summary>A one-off booking that covers the whole day rather than a time span.</summary>
        public bool AllDayEvent
        {
            get => _allDayEvent;
            set => Set(ref _allDayEvent, value);
        }

        /// <summary>
        /// Everything except patterns the week grid cannot represent faithfully - those stay
        /// exactly as the server has them.
        /// </summary>
        public bool IsEditable => Kind != ScheduleEntryKind.OtherRecurring;

        public TimeSpan End => Start + Duration;

        /// <summary>True when the interval runs past midnight into the following day.</summary>
        public bool CrossesMidnight => End > TimeSpan.FromHours(24);

        public ScheduleEntry Clone()
        {
            var clone = (ScheduleEntry)MemberwiseClone();
            clone.PropertyChanged = null;
            return clone;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(Start) || name == nameof(Duration))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(End)));

            // Every settable field feeds the summary text, so lists showing it stay in step.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        }

        /// <summary>
        /// Bindable form of <see cref="Describe"/>. Lists bind to this rather than calling the
        /// method, which WPF cannot do.
        /// </summary>
        public string Description => Describe();

        /// <summary>
        /// Compares only what gets written back, so cosmetic differences do not trigger a save.
        /// Every field compared here must also be one the repository actually sends - a field that
        /// is compared but never written makes an entry compare as changed on every single save.
        /// </summary>
        public bool HasSameScheduleAs(ScheduleEntry other)
        {
            if (other == null || Kind != other.Kind) return false;
            if (!string.Equals(Subject ?? "", other.Subject ?? "", StringComparison.Ordinal)) return false;

            if (Kind == ScheduleEntryKind.SingleOccurrence)
            {
                return AllDayEvent == other.AllDayEvent &&
                       OccurrenceStart == other.OccurrenceStart &&
                       (AllDayEvent || OccurrenceEnd == other.OccurrenceEnd);
            }

            return Days == other.Days &&
                   Start == other.Start &&
                   Duration == other.Duration &&
                   RangeStart == other.RangeStart &&
                   RangeEnd == other.RangeEnd;
        }

        public string Describe()
        {
            switch (Kind)
            {
                case ScheduleEntryKind.SingleOccurrence:
                    if (OccurrenceStart == null) return Subject ?? "";
                    return AllDayEvent
                        ? $"{OccurrenceStart:yyyy-MM-dd} heldag"
                        : $"{OccurrenceStart:yyyy-MM-dd HH:mm}-{OccurrenceEnd:HH:mm}";

                case ScheduleEntryKind.Weekly:
                    var text = $"{Days.Describe()} {Format(Start)}-{Format(End)}";
                    if (RangeEnd.HasValue) text += $" (t.o.m. {RangeEnd:yyyy-MM-dd})";
                    return text;

                default:
                    return ServerDescription ?? "";
            }
        }

        public static string Format(TimeSpan t) =>
            $"{(int)t.TotalHours % 24:00}:{t.Minutes:00}" + (t.TotalHours >= 24 ? " (+1 dag)" : "");
    }
}
