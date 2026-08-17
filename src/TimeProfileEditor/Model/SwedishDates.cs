using System;
using System.Globalization;

namespace TimeProfileEditor.Model
{
    /// <summary>
    /// Swedish month names and ISO week numbers.
    ///
    /// Spelled out here rather than taken from the current culture, for the same reason the day
    /// names in <see cref="DayFlagsExtensions"/> are: Smart Client runs under whatever locale the
    /// machine happens to have, and every other string in this plugin is Swedish. A calendar
    /// heading that says "August" above a button that says "Lagg till tid" reads as a fault.
    /// </summary>
    internal static class SwedishDates
    {
        private static readonly string[] Months =
        {
            "januari", "februari", "mars", "april", "maj", "juni",
            "juli", "augusti", "september", "oktober", "november", "december"
        };

        public static string Month(int month) => Months[Math.Max(1, Math.Min(12, month)) - 1];

        /// <summary>"Augusti 2026" - the calendar heading.</summary>
        public static string MonthAndYear(DateTime date)
        {
            var name = Month(date.Month);
            return char.ToUpperInvariant(name[0]) + name.Substring(1) + " " + date.Year;
        }

        /// <summary>"onsdag 12 augusti 2026".</summary>
        public static string LongDate(DateTime date) =>
            $"{DayFlagsExtensions.LongName(date.DayOfWeek)} {date.Day} {Month(date.Month)} {date.Year}";

        /// <summary>"12 augusti".</summary>
        public static string ShortDate(DateTime date) => $"{date.Day} {Month(date.Month)}";

        /// <summary>"10/8" - the numeric form that fits above a day column.</summary>
        public static string DayAndMonth(DateTime date) => $"{date.Day}/{date.Month}";

        /// <summary>The Monday of the week <paramref name="date"/> falls in.</summary>
        public static DateTime MondayOf(DateTime date)
        {
            var offset = ((int)date.DayOfWeek + 6) % 7;   // Sunday is 0 in DayOfWeek, last here
            return date.Date.AddDays(-offset);
        }

        /// <summary>
        /// The week number printed in Swedish calendars, which is the ISO-8601 one.
        ///
        /// The three-day shift is the documented workaround for GetWeekOfYear: it counts weeks the
        /// way a culture does rather than the way ISO does, so without the shift the turn of the
        /// year lands in the wrong week - 31 December 2026 would be shown as week 1 of 2026 instead
        /// of week 53. An operator reading "v.1" next to a December date would rightly distrust the
        /// whole panel.
        /// </summary>
        public static int WeekNumber(DateTime date)
        {
            var day = CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(date);
            if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday) date = date.AddDays(3);

            return CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(
                date, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        }
    }
}
