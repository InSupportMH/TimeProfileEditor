using System;
using System.Globalization;

namespace TimeProfileEditor.Model
{
    /// <summary>
    /// Reading and writing a time of day as an operator types it.
    ///
    /// Lives here rather than in the converter that used to own it because the calendar panel needs
    /// the same parsing without going through a binding - and two parsers for the same field would
    /// eventually disagree about what "830" means.
    /// </summary>
    internal static class TimeText
    {
        /// <summary>"08:30". Hours run past 24 for an interval that crosses midnight.</summary>
        public static string Format(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}";

        /// <summary>
        /// Accepts "8", "830", "8:30" or "08.30", and returns null for anything else.
        ///
        /// Forgiving on purpose: operators type times far more often than they click, and a strict
        /// parser that silently rejects "830" reads as the field not working.
        /// </summary>
        public static TimeSpan? Parse(string text)
        {
            var value = text?.Trim().Replace('.', ':');
            if (string.IsNullOrEmpty(value)) return null;

            if (!value.Contains(":") && value.Length >= 3 && int.TryParse(value, out _))
                value = value.Insert(value.Length - 2, ":");

            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
                return Clamp(parsed);

            if (int.TryParse(value, out var hoursOnly) && hoursOnly >= 0 && hoursOnly <= 24)
                return TimeSpan.FromHours(hoursOnly);

            return null;
        }

        private static TimeSpan Clamp(TimeSpan value) =>
            value < TimeSpan.Zero ? TimeSpan.Zero
            : value > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24)
            : value;
    }
}
