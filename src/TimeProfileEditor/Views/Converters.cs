using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TimeProfileEditor.Model;
using TimeProfileEditor.ViewModels;

namespace TimeProfileEditor.Views
{
    /// <summary>
    /// Shows a TimeSpan as HH:mm and accepts "8", "830", "8:30" or "08.30" on the way back.
    /// Operators type times far more often than they click, so the parser is forgiving.
    /// </summary>
    internal sealed class TimeSpanToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is TimeSpan span ? TimeText.Format(span) : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            TimeText.Parse(value as string) ?? (object)Binding.DoNothing;
    }

    /// <summary>
    /// Shows a date as yyyy-MM-dd and accepts a few shorthands on the way back. Empty means
    /// "no date", which the caller reads as "tills vidare" for a validity range.
    /// </summary>
    internal sealed class DateToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime date) return date.ToString("yyyy-MM-dd");
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = (value as string)?.Trim();
            if (string.IsNullOrEmpty(text))
                return targetType == typeof(DateTime?) ? (object)null : Binding.DoNothing;

            if (DateTime.TryParseExact(text, new[] { "yyyy-MM-dd", "yyyyMMdd", "yy-MM-dd" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                return exact.Date;

            return DateTime.TryParse(text, culture, DateTimeStyles.None, out var parsed)
                ? parsed.Date
                : Binding.DoNothing;
        }
    }

    /// <summary>Shows an element only for entries of the kind named in ConverterParameter.</summary>
    internal sealed class KindToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var entry = value as Model.ScheduleEntry;
            var wanted = parameter as string;
            if (entry == null || string.IsNullOrEmpty(wanted)) return Visibility.Collapsed;

            return string.Equals(entry.Kind.ToString(), wanted, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    internal sealed class NullOrEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hasContent = value is string s ? !string.IsNullOrWhiteSpace(s) : value != null;
            if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
                hasContent = !hasContent;
            return hasContent ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Enables a control while a box is unticked - the "Heldag" case.</summary>
    internal sealed class NotBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            !(value is bool flag && flag);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            !(value is bool flag && flag);
    }

    internal sealed class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    internal sealed class SeverityToBrushConverter : IValueConverter
    {
        private static readonly Brush Info = New(0x8A, 0xB4, 0xD8);
        private static readonly Brush Success = New(0x7C, 0xD9, 0x92);
        private static readonly Brush Warning = New(0xE8, 0xC0, 0x6A);
        private static readonly Brush Error = New(0xF0, 0x8A, 0x8A);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            switch (value)
            {
                case StatusSeverity.Success: return Success;
                case StatusSeverity.Warning: return Warning;
                case StatusSeverity.Error: return Error;
                default: return Info;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static Brush New(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
