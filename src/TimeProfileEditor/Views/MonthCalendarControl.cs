using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TimeProfileEditor.Model;

namespace TimeProfileEditor.Views
{
    /// <summary>
    /// A month of real dates, drawn Monday-first with week numbers, showing what the profile
    /// actually covers on each day.
    ///
    /// The week grid next to it edits a *pattern*; this shows the *outcome*. That difference is the
    /// whole point of having both: a pattern whose validity period ended last month still looks
    /// perfectly healthy in the week grid, and only a calendar of real dates shows that it stopped
    /// applying. Each day carries a small 00:00-24:00 strip with the covered stretches filled in, so
    /// a glance down a column answers "does this profile cover Saturdays" without reading anything.
    ///
    /// Drawn rather than composed, like <see cref="WeekScheduleControl"/>: dragging a date range
    /// redraws every cell on every mouse move, and there are 42 of them.
    ///
    /// It selects dates; it never edits. Everything that changes the profile is a command on the
    /// view model, so what a click does is decided in one place rather than in a mouse handler.
    /// </summary>
    internal sealed class MonthCalendarControl : FrameworkElement
    {
        private const double WeekColumn = 30;
        private const double HeaderHeight = 22;
        private const double RowHeight = 46;
        private const int Rows = 6;

        private readonly Typeface _typeface = new Typeface("Segoe UI");

        // The Smart Client's dark chrome, matching the week grid beside it.
        private static readonly Brush Surface = Frozen(Color.FromRgb(0x24, 0x26, 0x29));
        private static readonly Brush DayCell = Frozen(Color.FromRgb(0x2A, 0x2C, 0x30));
        private static readonly Brush WeekendCell = Frozen(Color.FromRgb(0x25, 0x27, 0x2B));
        private static readonly Brush OutsideCell = Frozen(Color.FromRgb(0x21, 0x23, 0x26));
        private static readonly Brush SelectedCell = Frozen(Color.FromArgb(0x66, 0x2E, 0x8B, 0xCE));
        private static readonly Brush HeaderText = Frozen(Color.FromRgb(0xD8, 0xDB, 0xE0));
        private static readonly Brush DayText = Frozen(Color.FromRgb(0xE8, 0xEA, 0xED));
        private static readonly Brush DimText = Frozen(Color.FromRgb(0x63, 0x68, 0x70));
        private static readonly Brush WeekText = Frozen(Color.FromRgb(0x7C, 0x83, 0x8C));
        private static readonly Brush StripEmpty = Frozen(Color.FromRgb(0x15, 0x17, 0x19));
        private static readonly Brush WeeklyFill = Frozen(Color.FromRgb(0x35, 0x7F, 0xB8));
        private static readonly Brush WeeklyHighlight = Frozen(Color.FromRgb(0x8F, 0xCE, 0xFF));
        private static readonly Brush DateFill = Frozen(Color.FromRgb(0xD0, 0x94, 0x3E));

        private static readonly Pen CellPen = FrozenPen(Color.FromRgb(0x34, 0x37, 0x3C), 1);
        private static readonly Pen TodayPen = FrozenPen(Color.FromRgb(0xA8, 0xDC, 0xFF), 1.5);
        private static readonly Pen SelectedPen = FrozenPen(Color.FromRgb(0x9C, 0xD3, 0xF5), 1);

        private IReadOnlyList<ScheduleEntry> _weekly;
        private IReadOnlyList<ScheduleEntry> _dates;
        private ObservableCollection<DateTime> _selection;

        private DateTime? _anchor;
        private HashSet<DateTime> _dragBase;
        private bool _dragging;
        private DateTime? _hovered;

        public MonthCalendarControl()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Cursors.Arrow;
        }

        /// <summary>The month on show. Always normalised to its first day.</summary>
        public static readonly DependencyProperty MonthProperty = DependencyProperty.Register(
            nameof(Month), typeof(DateTime), typeof(MonthCalendarControl),
            new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.AffectsRender));

        public DateTime Month
        {
            get => (DateTime)GetValue(MonthProperty);
            set => SetValue(MonthProperty, value);
        }

        /// <summary>
        /// The entry selected elsewhere in the editor, drawn in a brighter colour.
        ///
        /// This is what ties the two views together: clicking a block in the week grid lights up
        /// every date in the month it lands on, including the ones its validity period rules out.
        /// </summary>
        public static readonly DependencyProperty HighlightEntryProperty = DependencyProperty.Register(
            nameof(HighlightEntry), typeof(ScheduleEntry), typeof(MonthCalendarControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public ScheduleEntry HighlightEntry
        {
            get => (ScheduleEntry)GetValue(HighlightEntryProperty);
            set => SetValue(HighlightEntryProperty, value);
        }

        /// <summary>Raised on double-click, so the editor can open whatever is on that date.</summary>
        public event EventHandler<DateTime> DateActivated;

        public void SetSource(IReadOnlyList<ScheduleEntry> weekly, IReadOnlyList<ScheduleEntry> dates,
            ObservableCollection<DateTime> selection)
        {
            _weekly = weekly;
            _dates = dates;
            _selection = selection;
            InvalidateVisual();
        }

        public void Refresh() => InvalidateVisual();

        /// <summary>
        /// A fixed height, because the parent is a stack panel and a FrameworkElement that measures
        /// itself as nothing would be arranged as nothing. Six week rows always, whether the month
        /// needs them or not, so the panel below does not jump about between months.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width;
            return new Size(width, HeaderHeight + Rows * RowHeight);
        }

        // ---- drawing -----------------------------------------------------------------------

        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= WeekColumn + 40 || height <= HeaderHeight + 40) return;

            var cellWidth = (width - WeekColumn) / 7.0;
            var cellHeight = (height - HeaderHeight) / Rows;

            dc.DrawRectangle(Surface, null, new Rect(0, 0, width, height));

            for (var column = 0; column < 7; column++)
            {
                var day = DayFlagsExtensions.WeekOrder[column];
                DrawText(dc, DayFlagsExtensions.ShortName(day), HeaderText, 11,
                    new Point(WeekColumn + column * cellWidth + cellWidth / 2, 4), centred: true);
            }

            var entries = AllEntries();
            var first = FirstVisibleDate();

            for (var row = 0; row < Rows; row++)
            {
                var rowStart = first.AddDays(row * 7);
                var y = HeaderHeight + row * cellHeight;

                DrawText(dc, "v." + SwedishDates.WeekNumber(rowStart), WeekText, 10,
                    new Point(WeekColumn / 2, y + cellHeight / 2 - 7), centred: true);

                for (var column = 0; column < 7; column++)
                    DrawDay(dc, rowStart.AddDays(column), entries,
                        new Rect(WeekColumn + column * cellWidth, y, cellWidth, cellHeight));
            }
        }

        private void DrawDay(DrawingContext dc, DateTime date, IReadOnlyList<ScheduleEntry> entries, Rect cell)
        {
            var inMonth = date.Month == Month.Month && date.Year == Month.Year;
            var weekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
            var selected = IsSelected(date);

            var background = selected ? SelectedCell : !inMonth ? OutsideCell : weekend ? WeekendCell : DayCell;
            dc.DrawRectangle(background, selected ? SelectedPen : CellPen, Snapped(cell));

            if (date == DateTime.Today)
                dc.DrawRectangle(null, TodayPen, Snapped(Deflate(cell, 2)));

            DrawText(dc, date.Day.ToString(CultureInfo.InvariantCulture),
                inMonth ? DayText : DimText, 12, new Point(cell.X + 5, cell.Y + 3));

            var coverage = Coverage.For(date, entries);
            var strip = new Rect(cell.X + 4, cell.Bottom - 11, Math.Max(1, cell.Width - 8), 7);
            dc.DrawRectangle(StripEmpty, null, strip);

            // Weekly first, one-off dates over the top. They overlap often - a booking added on a
            // day the pattern already covers is the ordinary case - and whichever is drawn last is
            // the one that shows. A one-off is the exception on that date and the thing worth
            // seeing; the pattern is visible on every other day of its column anyway.
            foreach (var span in coverage.Spans.Where(s => s.Source == CoverageSource.Weekly)
                         .Concat(coverage.Spans.Where(s => s.Source == CoverageSource.Date)))
            {
                var from = strip.X + span.From.TotalHours / 24.0 * strip.Width;
                var to = strip.X + span.To.TotalHours / 24.0 * strip.Width;

                var fill = span.Source == CoverageSource.Date
                    ? DateFill
                    : ReferenceEquals(span.Entry, HighlightEntry) ? WeeklyHighlight : WeeklyFill;

                dc.DrawRectangle(fill, null,
                    new Rect(from, strip.Y, Math.Max(1.5, to - from), strip.Height));
            }
        }

        private IReadOnlyList<ScheduleEntry> AllEntries()
        {
            if (_weekly == null) return _dates ?? (IReadOnlyList<ScheduleEntry>)new List<ScheduleEntry>();
            if (_dates == null) return _weekly;
            return _weekly.Concat(_dates).ToList();
        }

        /// <summary>The Monday on or before the first of the month.</summary>
        private DateTime FirstVisibleDate()
        {
            var first = new DateTime(Month.Year, Month.Month, 1);
            return first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        }

        // ---- selection ---------------------------------------------------------------------

        private bool IsSelected(DateTime date) => _selection != null && _selection.Contains(date.Date);

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            if (_selection == null) return;

            var point = e.GetPosition(this);

            if (point.Y < HeaderHeight)
            {
                // The whole column, but only the dates that belong to this month. The leading and
                // trailing cells are real dates in the neighbouring months, and someone clicking
                // "Mån" while looking at August does not mean the last Monday of July.
                var weekday = WeekdayAt(point.X);
                if (weekday.HasValue)
                    Replace(VisibleDates().Where(d => d.DayOfWeek == weekday.Value &&
                                                      d.Month == Month.Month && d.Year == Month.Year));
                e.Handled = true;
                return;
            }

            if (point.X < WeekColumn)
            {
                // A week, all seven days of it - including any that spill over the month boundary,
                // because a week that is split across two months is still one week to whoever runs
                // the site.
                var row = RowAt(point.Y);
                if (row.HasValue)
                {
                    var start = FirstVisibleDate().AddDays(row.Value * 7);
                    Replace(Enumerable.Range(0, 7).Select(offset => start.AddDays(offset)));
                }

                e.Handled = true;
                return;
            }

            var date = DateAt(point);
            if (date == null) return;

            // FrameworkElement has no double-click of its own - that lives on Control - so the
            // count on the event is what distinguishes the second click from the first.
            if (e.ClickCount == 2)
            {
                DateActivated?.Invoke(this, date.Value);
                e.Handled = true;
                return;
            }

            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            _dragBase = control ? new HashSet<DateTime>(_selection) : new HashSet<DateTime>();

            if (shift && _anchor.HasValue)
            {
                Replace(_dragBase.Concat(Between(_anchor.Value, date.Value)));
            }
            else if (control)
            {
                if (_dragBase.Contains(date.Value)) _dragBase.Remove(date.Value);
                else _dragBase.Add(date.Value);

                Replace(_dragBase);
                _anchor = date;
            }
            else
            {
                Replace(new[] { date.Value });
                _anchor = date;
            }

            _dragging = true;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var point = e.GetPosition(this);

            if (_dragging && _anchor.HasValue)
            {
                var under = DateAt(point);
                if (under != null)
                    Replace(_dragBase.Concat(Between(_anchor.Value, under.Value)));
                return;
            }

            var date = DateAt(point);
            Cursor = date == null && point.Y >= HeaderHeight && point.X >= WeekColumn
                ? Cursors.Arrow
                : Cursors.Hand;

            if (date == _hovered) return;
            _hovered = date;
            ToolTip = date == null ? null : Coverage.For(date.Value, AllEntries()).Describe();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (IsMouseCaptured) ReleaseMouseCapture();
            _dragging = false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || _selection == null || _selection.Count == 0) return;

            _selection.Clear();
            _anchor = null;
            InvalidateVisual();
            e.Handled = true;
        }

        private IEnumerable<DateTime> VisibleDates()
        {
            var first = FirstVisibleDate();
            return Enumerable.Range(0, Rows * 7).Select(offset => first.AddDays(offset));
        }

        private static IEnumerable<DateTime> Between(DateTime a, DateTime b)
        {
            var from = a < b ? a.Date : b.Date;
            var to = a < b ? b.Date : a.Date;

            for (var date = from; date <= to; date = date.AddDays(1)) yield return date;
        }

        /// <summary>
        /// Makes the bound collection hold exactly these dates, and only touches it when that
        /// changes something - a drag crosses the same cell many times, and rebuilding the
        /// collection on every mouse move would fire a notification storm at the view model.
        /// </summary>
        private void Replace(IEnumerable<DateTime> dates)
        {
            var wanted = new SortedSet<DateTime>(dates.Select(d => d.Date));
            if (_selection.Count == wanted.Count && _selection.All(wanted.Contains)) return;

            _selection.Clear();
            foreach (var date in wanted) _selection.Add(date);
            InvalidateVisual();
        }

        // ---- geometry ----------------------------------------------------------------------

        private DateTime? DateAt(Point point)
        {
            if (point.Y < HeaderHeight || point.X < WeekColumn) return null;

            var row = RowAt(point.Y);
            var column = WeekdayColumnAt(point.X);
            if (row == null || column == null) return null;

            return FirstVisibleDate().AddDays(row.Value * 7 + column.Value);
        }

        private int? RowAt(double y)
        {
            var cellHeight = (ActualHeight - HeaderHeight) / Rows;
            if (cellHeight <= 0) return null;

            var row = (int)Math.Floor((y - HeaderHeight) / cellHeight);
            return row < 0 || row >= Rows ? (int?)null : row;
        }

        private int? WeekdayColumnAt(double x)
        {
            var cellWidth = (ActualWidth - WeekColumn) / 7.0;
            if (cellWidth <= 0) return null;

            var column = (int)Math.Floor((x - WeekColumn) / cellWidth);
            return column < 0 || column > 6 ? (int?)null : column;
        }

        private DayOfWeek? WeekdayAt(double x)
        {
            var column = WeekdayColumnAt(x);
            return column == null ? (DayOfWeek?)null : DayFlagsExtensions.WeekOrder[column.Value];
        }

        private static Rect Deflate(Rect rect, double by) =>
            new Rect(rect.X + by, rect.Y + by, Math.Max(0, rect.Width - 2 * by), Math.Max(0, rect.Height - 2 * by));

        /// <summary>Aligns to the device pixel grid so the cell borders stay hairlines.</summary>
        private static Rect Snapped(Rect rect) =>
            new Rect(Math.Round(rect.X) + 0.5, Math.Round(rect.Y) + 0.5,
                Math.Max(1, Math.Round(rect.Width) - 1), Math.Max(1, Math.Round(rect.Height) - 1));

        private void DrawText(DrawingContext dc, string text, Brush brush, double size, Point at,
            bool centred = false)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var origin = at;
            if (centred) origin.X -= formatted.Width / 2;
            dc.DrawText(formatted, origin);
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Color color, double thickness)
        {
            var pen = new Pen(Frozen(color), thickness);
            pen.Freeze();
            return pen;
        }
    }
}
