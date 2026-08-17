using System;
using System.Collections.Generic;
using System.Linq;

namespace TimeProfileEditor.Model
{
    internal enum CoverageSource
    {
        /// <summary>From a weekly pattern, so it comes back every week.</summary>
        Weekly,

        /// <summary>From a one-off booking that applies to this date alone.</summary>
        Date
    }

    /// <summary>One stretch of a single day that the profile covers.</summary>
    internal struct CoverageSpan
    {
        /// <summary>Offset from midnight at the start of the day. Always inside 00:00-24:00.</summary>
        public TimeSpan From;

        public TimeSpan To;
        public CoverageSource Source;

        /// <summary>The entry that produced it, so the calendar can pick out the selected one.</summary>
        public ScheduleEntry Entry;
    }

    /// <summary>What a profile covers on one particular date.</summary>
    internal sealed class DayCoverage
    {
        public DateTime Date { get; set; }

        /// <summary>In start order, and not merged - overlapping intervals stay separate.</summary>
        public IReadOnlyList<CoverageSpan> Spans { get; set; } = new List<CoverageSpan>();

        /// <summary>Time covered, counting an overlap once. Merged for this, unlike Spans.</summary>
        public TimeSpan Total { get; set; }

        public bool IsCovered => Spans.Count > 0;
        public bool HasWeekly => Spans.Any(s => s.Source == CoverageSource.Weekly);
        public bool HasDate => Spans.Any(s => s.Source == CoverageSource.Date);

        /// <summary>The whole day, one line per interval - what the calendar shows on hover.</summary>
        public string Describe()
        {
            var text = SwedishDates.LongDate(Date);
            if (!IsCovered) return text + Environment.NewLine + "Ingen tid";

            foreach (var span in Spans)
            {
                var label = span.Source == CoverageSource.Date ? "enstaka datum" : "veckomönster";
                text += Environment.NewLine +
                        (span.From == TimeSpan.Zero && span.To >= TimeSpan.FromHours(24)
                            ? $"Heldag ({label})"
                            : $"{TimeText.Format(span.From)}–{TimeText.Format(span.To)} ({label})");
            }

            return text;
        }
    }

    /// <summary>
    /// Works out which real dates a profile's entries land on.
    ///
    /// Only the two kinds this plugin writes are resolved: weekly patterns and one-off dates.
    /// Anything the editor treats as read-only - a daily, monthly or yearly recurrence, or a run
    /// limited by a number of occurrences - is left out entirely, because the client never read the
    /// fields that would say when those actually fall. Guessing would be worse than the gap: a
    /// calendar that quietly drew a monthly pattern on the wrong day would be believed.
    ///
    /// The calendar says out loud when a profile holds any of those, so a blank day means "nothing
    /// this panel can draw" rather than "nothing at all".
    /// </summary>
    internal static class Coverage
    {
        private static readonly TimeSpan Day = TimeSpan.FromHours(24);

        /// <summary>Whether a weekly pattern is in force on <paramref name="date"/>.</summary>
        public static bool AppliesOn(ScheduleEntry entry, DateTime date)
        {
            if (entry == null || entry.Kind != ScheduleEntryKind.Weekly) return false;

            date = date.Date;
            if (!entry.Days.Has(date.DayOfWeek)) return false;
            if (date < entry.RangeStart.Date) return false;

            return !entry.RangeEnd.HasValue || date <= entry.RangeEnd.Value.Date;
        }

        public static DayCoverage For(DateTime date, IEnumerable<ScheduleEntry> entries)
        {
            date = date.Date;
            var spans = new List<CoverageSpan>();
            var previous = date.AddDays(-1);

            foreach (var entry in entries ?? Enumerable.Empty<ScheduleEntry>())
            {
                switch (entry.Kind)
                {
                    case ScheduleEntryKind.Weekly:
                        if (AppliesOn(entry, date))
                            Add(spans, entry.Start, entry.End, CoverageSource.Weekly, entry);

                        // An interval running past midnight covers the first hours of the day after
                        // the one its pattern names - so the range check belongs to that pattern
                        // day, not to this one. The week grid splits it the same way.
                        if (entry.CrossesMidnight && AppliesOn(entry, previous))
                            Add(spans, TimeSpan.Zero, entry.End - Day, CoverageSource.Weekly, entry);
                        break;

                    case ScheduleEntryKind.SingleOccurrence:
                        AddOccurrence(spans, entry, date);
                        break;
                }
            }

            spans.Sort((a, b) => a.From.CompareTo(b.From));
            return new DayCoverage { Date = date, Spans = spans, Total = Union(spans) };
        }

        private static void AddOccurrence(List<CoverageSpan> spans, ScheduleEntry entry, DateTime date)
        {
            if (entry.OccurrenceStart == null) return;
            var start = entry.OccurrenceStart.Value;

            // An all-day booking is stored with start and end on the same midnight - the flag, not
            // the span, is what makes it cover the day. See TimeProfileRepository.AddSingleOccurrence.
            if (entry.AllDayEvent)
            {
                if (start.Date == date) Add(spans, TimeSpan.Zero, Day, CoverageSource.Date, entry);
                return;
            }

            var end = entry.OccurrenceEnd ?? start.AddHours(1);
            if (end <= start) end = start.AddHours(1);

            var midnight = date.AddDays(1);
            if (end <= date || start >= midnight) return;

            Add(spans,
                start < date ? TimeSpan.Zero : start - date,
                end > midnight ? Day : end - date,
                CoverageSource.Date, entry);
        }

        private static void Add(List<CoverageSpan> spans, TimeSpan from, TimeSpan to,
            CoverageSource source, ScheduleEntry entry)
        {
            if (from < TimeSpan.Zero) from = TimeSpan.Zero;
            if (to > Day) to = Day;
            if (to <= from) return;

            spans.Add(new CoverageSpan { From = from, To = to, Source = source, Entry = entry });
        }

        /// <summary>Total length of the union, so two overlapping intervals do not count twice.</summary>
        private static TimeSpan Union(IEnumerable<CoverageSpan> spans)
        {
            var total = TimeSpan.Zero;
            var reached = TimeSpan.Zero;

            foreach (var span in spans.OrderBy(s => s.From))
            {
                var from = span.From > reached ? span.From : reached;
                if (span.To <= from) continue;

                total += span.To - from;
                reached = span.To;
            }

            return total;
        }
    }
}
