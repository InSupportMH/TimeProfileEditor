using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TimeProfileEditor.Model;
using TimeProfileEditor.Services;
using TimeProfileEditor.ViewModels;

namespace TimeProfileEditor.Views
{
    /// <summary>
    /// A Monday-first week seen as seven day columns over 24 hours, drawn directly rather than
    /// composed from elements - there is one rectangle per day per interval and redraws happen on
    /// every mouse move during a drag, which is exactly the case retained visuals handle badly.
    ///
    /// One interval belongs to several days at once (it is a single weekly recurrence with a day
    /// mask), so dragging any of its blocks moves all of them. The other blocks of the dragged
    /// interval are outlined while dragging to make that obvious rather than surprising.
    ///
    /// THE COLUMNS ARE REAL DATES
    ///
    /// <see cref="Week"/> says which week, and the header carries each column's date. That is what
    /// lets a one-off booking appear at all - it belongs to 15 August, not to "Saturday", and a grid
    /// of bare weekday names has nowhere to put it.
    ///
    /// Naming the dates also makes two things the grid used to get away with into errors, and both
    /// are handled rather than left: a weekly pattern whose validity period does not cover the shown
    /// week is drawn faded instead of as though it were in force, and an interval running past
    /// midnight now continues onto the *following* date rather than wrapping from Sunday round to
    /// the Monday of the same week.
    /// </summary>
    internal sealed class WeekScheduleControl : FrameworkElement
    {
        private const double GutterWidth = 46;
        private const double HeaderHeight = 36;
        private const double EdgeGrip = 6;
        private const double DragThreshold = 3;

        private static readonly TimeSpan Day = TimeSpan.FromHours(24);

        private readonly Typeface _typeface = new Typeface("Segoe UI");

        // Chosen to sit on the Smart Client's dark chrome without competing with video.
        private static readonly Brush GridBackground = Frozen(Color.FromRgb(0x24, 0x26, 0x29));
        private static readonly Brush AlternateColumn = Frozen(Color.FromRgb(0x2A, 0x2C, 0x30));
        private static readonly Brush HourLine = Frozen(Color.FromRgb(0x3A, 0x3D, 0x42));
        private static readonly Brush HalfHourLine = Frozen(Color.FromRgb(0x30, 0x33, 0x37));
        private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x9A, 0x9E, 0xA6));
        private static readonly Brush HeaderBrush = Frozen(Color.FromRgb(0xD8, 0xDB, 0xE0));
        private static readonly Brush BlockFill = Frozen(Color.FromArgb(0xD8, 0x25, 0x6F, 0xA8));
        private static readonly Brush BlockBorder = Frozen(Color.FromRgb(0x4F, 0xA3, 0xDA));
        private static readonly Brush SelectedFill = Frozen(Color.FromArgb(0xF0, 0x33, 0x8C, 0xCE));
        private static readonly Brush SelectedBorder = Frozen(Color.FromRgb(0xA8, 0xDC, 0xFF));
        private static readonly Brush ReadOnlyFill = Frozen(Color.FromArgb(0x90, 0x5A, 0x5E, 0x66));
        private static readonly Brush ReadOnlyBorder = Frozen(Color.FromRgb(0x77, 0x7C, 0x85));
        private static readonly Brush BlockText = Frozen(Colors.White);

        // One-off dates, in the same amber the month calendar uses for them. The two panels are
        // read together and a booking that changed colour between them would read as two things.
        private static readonly Brush DateFill = Frozen(Color.FromArgb(0xD8, 0xC0, 0x82, 0x30));
        private static readonly Brush DateBorder = Frozen(Color.FromRgb(0xE3, 0xAA, 0x58));
        private static readonly Brush DateSelectedFill = Frozen(Color.FromArgb(0xF0, 0xD8, 0x94, 0x3E));
        private static readonly Brush DateSelectedBorder = Frozen(Color.FromRgb(0xFF, 0xD9, 0x9E));

        // A weekly pattern outside its validity period. Kept visible and editable - it is still in
        // the profile and the operator may want to extend it - but it must not look like time that
        // applies this week, because it is not.
        private static readonly Brush LapsedFill = Frozen(Color.FromArgb(0x2A, 0x4F, 0xA3, 0xDA));
        private static readonly Brush LapsedBorder = Frozen(Color.FromArgb(0x99, 0x4F, 0xA3, 0xDA));
        private static readonly Brush LapsedText = Frozen(Color.FromRgb(0x9A, 0x9E, 0xA6));

        private static readonly Brush TodayColumn = Frozen(Color.FromArgb(0x22, 0x4F, 0xA3, 0xDA));
        private static readonly Brush TodayHeader = Frozen(Color.FromRgb(0xA8, 0xDC, 0xFF));

        private ObservableCollection<ScheduleEntry> _entries;
        private ObservableCollection<ScheduleEntry> _readOnlyEntries;
        private ObservableCollection<ScheduleEntry> _dateEntries;

        private readonly List<Block> _blocks = new List<Block>();

        private DragMode _mode = DragMode.None;
        private ScheduleEntry _dragEntry;
        private Point _dragOrigin;
        private TimeSpan _dragStartValue;
        private TimeSpan _dragDurationValue;
        private DayOfWeek _dragOriginDay;
        private bool _dragConfirmed;
        private bool _createdDuringDrag;

        public WeekScheduleControl()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        public static readonly DependencyProperty SelectedEntryProperty = DependencyProperty.Register(
            nameof(SelectedEntry), typeof(ScheduleEntry), typeof(WeekScheduleControl),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((WeekScheduleControl)d).InvalidateVisual()));

        public ScheduleEntry SelectedEntry
        {
            get => (ScheduleEntry)GetValue(SelectedEntryProperty);
            set => SetValue(SelectedEntryProperty, value);
        }

        public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
            nameof(IsReadOnly), typeof(bool), typeof(WeekScheduleControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        /// <summary>
        /// Any date in the week to show; the grid starts from that week's Monday. Default is the
        /// current week, so the grid is on a real week before anything has been clicked.
        /// </summary>
        public static readonly DependencyProperty WeekProperty = DependencyProperty.Register(
            nameof(Week), typeof(DateTime), typeof(WeekScheduleControl),
            new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.AffectsRender));

        public DateTime Week
        {
            get => (DateTime)GetValue(WeekProperty);
            set => SetValue(WeekProperty, value);
        }

        /// <summary>
        /// Monday of the week being drawn. Resolved here rather than in the property default so an
        /// unbound grid still shows this week rather than the first week of year one, and so a
        /// client left open past midnight moves with the date.
        /// </summary>
        private DateTime Monday =>
            SwedishDates.MondayOf(Week == default(DateTime) ? DateTime.Today : Week);

        public void SetSource(ObservableCollection<ScheduleEntry> editable,
            ObservableCollection<ScheduleEntry> readOnly,
            ObservableCollection<ScheduleEntry> dates = null)
        {
            _entries = editable;
            _readOnlyEntries = readOnly;
            _dateEntries = dates;
            InvalidateVisual();
        }

        public void Refresh() => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= GutterWidth || height <= HeaderHeight) return;

            var gridLeft = GutterWidth;
            var gridTop = HeaderHeight;
            var gridWidth = width - GutterWidth;
            var gridHeight = height - HeaderHeight;
            var columnWidth = gridWidth / 7.0;

            dc.DrawRectangle(GridBackground, null, new Rect(gridLeft, gridTop, gridWidth, gridHeight));

            var monday = Monday;
            var today = DateTime.Today;

            for (var i = 0; i < 7; i++)
            {
                var x = gridLeft + i * columnWidth;
                var date = monday.AddDays(i);
                var isToday = date == today;

                if (i % 2 == 1)
                    dc.DrawRectangle(AlternateColumn, null, new Rect(x, gridTop, columnWidth, gridHeight));
                if (isToday)
                    dc.DrawRectangle(TodayColumn, null, new Rect(x, gridTop, columnWidth, gridHeight));

                var heading = isToday ? TodayHeader : HeaderBrush;
                DrawText(dc, DayFlagsExtensions.ShortName(date.DayOfWeek), heading, 12,
                    new Point(x + columnWidth / 2, 3), centred: true);
                DrawText(dc, SwedishDates.DayAndMonth(date), isToday ? TodayHeader : LabelBrush, 10.5,
                    new Point(x + columnWidth / 2, 19), centred: true);
            }

            // The week number sits where the hour gutter meets the header - the one place in the
            // grid that is not a time and not a day, and where a Swedish calendar puts it anyway.
            DrawText(dc, "v." + SwedishDates.WeekNumber(monday), LabelBrush, 11,
                new Point(GutterWidth / 2, 11), centred: true);

            var hourPen = new Pen(HourLine, 1);
            var halfPen = new Pen(HalfHourLine, 1);
            for (var hour = 0; hour <= 24; hour++)
            {
                var y = Snapped(gridTop + hour / 24.0 * gridHeight);
                dc.DrawLine(hourPen, new Point(gridLeft, y), new Point(width, y));

                if (hour < 24)
                {
                    var halfY = Snapped(gridTop + (hour + 0.5) / 24.0 * gridHeight);
                    dc.DrawLine(halfPen, new Point(gridLeft, halfY), new Point(width, halfY));
                    DrawText(dc, $"{hour:00}:00", LabelBrush, 10.5, new Point(GutterWidth - 8, y + 2), rightAligned: true);
                }
            }

            for (var i = 0; i <= 7; i++)
            {
                var x = Snapped(gridLeft + i * columnWidth);
                dc.DrawLine(hourPen, new Point(x, gridTop), new Point(x, height));
            }

            _blocks.Clear();

            // Draw order is by how much of the day each thing claims, widest first. An all-day
            // booking is a property of the date rather than an interval inside it, so it goes
            // behind - drawn on top it is a full-height block that hides every weekly pattern that
            // day, which is how a covered Thursday ends up looking like an empty one.
            DrawDates(dc, gridLeft, gridTop, columnWidth, gridHeight, wholeDay: true);

            foreach (var entry in _readOnlyEntries ?? Empty)
                DrawEntry(dc, entry, gridLeft, gridTop, columnWidth, gridHeight, readOnly: true);
            foreach (var entry in _entries ?? Empty)
                DrawEntry(dc, entry, gridLeft, gridTop, columnWidth, gridHeight, readOnly: IsReadOnly);

            // Timed one-off bookings last. These compete for the same rows as the weekly patterns,
            // and a booking made for one specific day is the more particular statement of the two.
            DrawDates(dc, gridLeft, gridTop, columnWidth, gridHeight, wholeDay: false);
        }

        /// <summary>
        /// Draws the one-off bookings that fall in the shown week, either the all-day ones or the
        /// timed ones - see the draw order in <see cref="OnRender"/>.
        ///
        /// The spans come from <see cref="Coverage"/> - the same calculation the month calendar
        /// draws its strips from - rather than from a second reading of OccurrenceStart and
        /// OccurrenceEnd here. An all-day booking is a flag rather than a span and a booking running
        /// past midnight belongs to two dates, and having two panels answer that independently is
        /// how they end up disagreeing on screen.
        /// </summary>
        private void DrawDates(DrawingContext dc, double gridLeft, double gridTop,
            double columnWidth, double gridHeight, bool wholeDay)
        {
            if (_dateEntries == null || _dateEntries.Count == 0) return;

            var monday = Monday;
            for (var column = 0; column < 7; column++)
            {
                var coverage = Coverage.For(monday.AddDays(column), _dateEntries);

                foreach (var span in coverage.Spans)
                {
                    if (span.Entry.AllDayEvent != wholeDay) continue;

                    var isSelected = ReferenceEquals(span.Entry, SelectedEntry);
                    var fill = isSelected ? DateSelectedFill : DateFill;
                    var pen = new Pen(isSelected ? DateSelectedBorder : DateBorder, isSelected ? 2 : 1);

                    var x = gridLeft + column * columnWidth;
                    var y = gridTop + span.From.TotalHours / 24.0 * gridHeight;
                    var h = Math.Max(2, (span.To - span.From).TotalHours / 24.0 * gridHeight);
                    var rect = new Rect(x + 2, y, columnWidth - 4, h);

                    dc.DrawRoundedRectangle(fill, pen, rect, 3, 3);

                    // Selectable, never draggable. A one-off is stored as two timestamps rather than
                    // a start plus a duration, so the drag code - which edits Start and Days - would
                    // move the block on screen and leave the booking where it was.
                    _blocks.Add(new Block { Entry = span.Entry, Day = span.Entry.OccurrenceStart?.DayOfWeek ?? DayOfWeek.Monday, Rect = rect, Fixed = true });

                    if (h >= 26 && rect.Width > 46)
                    {
                        var label = span.Entry.AllDayEvent
                            ? "Heldag"
                            : $"{TimeText.Format(span.From)}–{TimeText.Format(span.To)}";
                        DrawText(dc, label, BlockText, 11, new Point(rect.X + 5, rect.Y + 4),
                            maxWidth: rect.Width - 8);
                    }
                }
            }
        }

        private static readonly ObservableCollection<ScheduleEntry> Empty = new ObservableCollection<ScheduleEntry>();

        private void DrawEntry(DrawingContext dc, ScheduleEntry entry, double gridLeft, double gridTop,
            double columnWidth, double gridHeight, bool readOnly)
        {
            if (entry.Kind == ScheduleEntryKind.SingleOccurrence) return;

            var monday = Monday;

            for (var column = 0; column < 7; column++)
            {
                var date = monday.AddDays(column);

                // An interval running past midnight is drawn as the part before midnight plus a
                // continuation at the top of the *next date's* column - so the tail of a Sunday
                // night pass leaves this week rather than reappearing on its Monday, and the tail
                // shown on Monday belongs to the Sunday before it.
                foreach (var segment in SegmentsOn(entry, date))
                {
                    // Whether the pattern is in force is a question about the day it recurs on, not
                    // about the day its tail happens to land in.
                    var patternDate = segment.IsContinuation ? date.AddDays(-1) : date;
                    var lapsed = entry.Kind == ScheduleEntryKind.Weekly &&
                                 !Coverage.AppliesOn(entry, patternDate);

                    var isSelected = ReferenceEquals(entry, SelectedEntry);
                    var fill = readOnly ? ReadOnlyFill
                        : lapsed ? LapsedFill
                        : isSelected ? SelectedFill : BlockFill;
                    var border = readOnly ? ReadOnlyBorder
                        : lapsed ? (isSelected ? SelectedBorder : LapsedBorder)
                        : isSelected ? SelectedBorder : BlockBorder;
                    var pen = new Pen(border, isSelected ? 2 : 1);
                    if (lapsed && !isSelected) pen.DashStyle = DashStyles.Dash;

                    var x = gridLeft + column * columnWidth;
                    var y = gridTop + segment.From.TotalHours / 24.0 * gridHeight;
                    var h = Math.Max(2, (segment.To - segment.From).TotalHours / 24.0 * gridHeight);
                    var rect = new Rect(x + 2, y, columnWidth - 4, h);

                    dc.DrawRoundedRectangle(fill, pen, rect, 3, 3);

                    if (!readOnly && !segment.IsContinuation)
                        _blocks.Add(new Block { Entry = entry, Day = date.DayOfWeek, Rect = rect });

                    if (h >= 26 && rect.Width > 46)
                    {
                        var label = $"{ScheduleEntry.Format(entry.Start).Substring(0, 5)}–" +
                                    $"{ScheduleEntry.Format(entry.End).Substring(0, 5)}";
                        DrawText(dc, label, lapsed ? LapsedText : BlockText, 11,
                            new Point(rect.X + 5, rect.Y + 4), maxWidth: rect.Width - 8);
                    }
                }
            }
        }

        private struct Segment
        {
            public TimeSpan From;
            public TimeSpan To;
            public bool IsContinuation;
        }

        /// <summary>
        /// What <paramref name="entry"/> puts on <paramref name="date"/>: its own interval if the
        /// pattern recurs that weekday, plus the tail of the previous day's occurrence if that one
        /// ran past midnight. Says nothing about whether the pattern is still valid - that is a
        /// separate question, deliberately, so a lapsed pattern can be drawn rather than dropped.
        /// </summary>
        private static IEnumerable<Segment> SegmentsOn(ScheduleEntry entry, DateTime date)
        {
            if (entry.Days.Has(date.DayOfWeek))
            {
                var end = entry.End;
                yield return new Segment { From = entry.Start, To = end <= Day ? end : Day };
            }

            if (entry.CrossesMidnight && entry.Days.Has(date.AddDays(-1).DayOfWeek))
                yield return new Segment
                {
                    From = TimeSpan.Zero,
                    To = entry.End - Day,
                    IsContinuation = true
                };
        }

        private sealed class Block
        {
            public ScheduleEntry Entry;
            public DayOfWeek Day;
            public Rect Rect;

            /// <summary>Selectable but not draggable - see the one-off bookings in DrawDates.</summary>
            public bool Fixed;
        }

        private enum DragMode
        {
            None,
            Move,
            ResizeStart,
            ResizeEnd,
            Create
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();

            var point = e.GetPosition(this);
            var hit = HitTest(point);

            if (hit != null)
            {
                SelectedEntry = hit.Entry;
                if (IsReadOnly || hit.Fixed)
                {
                    // Selecting is the whole interaction for these. The click still counts as
                    // handled, or the grid would go on to treat it as the start of a new interval
                    // drawn on top of the block just clicked.
                    e.Handled = true;
                    return;
                }

                _dragEntry = hit.Entry;
                _dragOrigin = point;
                _dragStartValue = hit.Entry.Start;
                _dragDurationValue = hit.Entry.Duration;
                _dragOriginDay = hit.Day;
                _dragConfirmed = false;
                _createdDuringDrag = false;

                if (point.Y - hit.Rect.Top <= EdgeGrip) _mode = DragMode.ResizeStart;
                else if (hit.Rect.Bottom - point.Y <= EdgeGrip) _mode = DragMode.ResizeEnd;
                else _mode = DragMode.Move;

                CaptureMouse();
                e.Handled = true;
                return;
            }

            SelectedEntry = null;
            if (IsReadOnly || _entries == null || !IsInsideGrid(point)) return;

            _mode = DragMode.Create;
            _dragOrigin = point;
            _dragConfirmed = false;
            _createdDuringDrag = false;
            _dragEntry = null;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var point = e.GetPosition(this);

            if (_mode == DragMode.None)
            {
                var hover = HitTest(point);
                Cursor = IsReadOnly || hover == null || hover.Fixed
                    ? Cursors.Arrow
                    : point.Y - hover.Rect.Top <= EdgeGrip || hover.Rect.Bottom - point.Y <= EdgeGrip
                        ? Cursors.SizeNS
                        : Cursors.SizeAll;
                return;
            }

            if (!_dragConfirmed)
            {
                if (Math.Abs(point.Y - _dragOrigin.Y) < DragThreshold &&
                    Math.Abs(point.X - _dragOrigin.X) < DragThreshold) return;
                _dragConfirmed = true;
            }

            if (_mode == DragMode.Create && !_createdDuringDrag)
            {
                var day = DayAt(_dragOrigin.X);
                var start = SnapTime(TimeAt(_dragOrigin.Y));
                _dragEntry = new ScheduleEntry
                {
                    Kind = ScheduleEntryKind.Weekly,
                    Days = day.ToFlag(),
                    Start = start,
                    Duration = TimeProfileEditorViewModel.Snap,
                    Subject = "Vald tid"
                };
                _dragStartValue = start;
                _dragDurationValue = TimeProfileEditorViewModel.Snap;
                _entries.Add(_dragEntry);
                SelectedEntry = _dragEntry;
                _createdDuringDrag = true;
            }

            if (_dragEntry == null) return;

            var deltaTime = SnapTime(TimeAt(point.Y)) - SnapTime(TimeAt(_dragOrigin.Y));

            switch (_mode)
            {
                case DragMode.Move:
                {
                    var start = Clamp(_dragStartValue + deltaTime, TimeSpan.Zero, Day - _dragDurationValue);
                    _dragEntry.Start = start;

                    // A single-day interval follows the pointer sideways; a multi-day one keeps its
                    // days, because there is no sensible way to shift a mask by one column.
                    if (CountDays(_dragEntry.Days) == 1)
                    {
                        var day = DayAt(point.X);
                        if (day != _dragOriginDay) _dragEntry.Days = day.ToFlag();
                    }

                    break;
                }

                case DragMode.ResizeStart:
                {
                    var end = _dragStartValue + _dragDurationValue;
                    var start = Clamp(_dragStartValue + deltaTime, TimeSpan.Zero, end - TimeProfileEditorViewModel.Snap);
                    _dragEntry.Start = start;
                    _dragEntry.Duration = end - start;
                    break;
                }

                case DragMode.ResizeEnd:
                case DragMode.Create:
                {
                    // Never let a drag reach a full 24 hours - the server would read that duration
                    // as a number of days. See TimeProfileRepository.MaxDuration.
                    var room = Day - _dragEntry.Start;
                    var ceiling = room < TimeProfileRepository.MaxDuration ? room : TimeProfileRepository.MaxDuration;
                    _dragEntry.Duration = Clamp(_dragDurationValue + deltaTime,
                        TimeProfileEditorViewModel.Snap, ceiling);
                    break;
                }
            }

            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (IsMouseCaptured) ReleaseMouseCapture();

            _mode = DragMode.None;
            _dragEntry = null;
            _dragConfirmed = false;
            _createdDuringDrag = false;
            InvalidateVisual();
        }

        private Block HitTest(Point point) =>
            _blocks.LastOrDefault(b => b.Rect.Contains(point));

        private bool IsInsideGrid(Point p) =>
            p.X >= GutterWidth && p.Y >= HeaderHeight && p.X <= ActualWidth && p.Y <= ActualHeight;

        private DayOfWeek DayAt(double x)
        {
            var columnWidth = (ActualWidth - GutterWidth) / 7.0;
            var index = (int)Math.Floor((x - GutterWidth) / columnWidth);
            index = Math.Max(0, Math.Min(6, index));
            return DayFlagsExtensions.WeekOrder[index];
        }

        private TimeSpan TimeAt(double y)
        {
            var gridHeight = ActualHeight - HeaderHeight;
            if (gridHeight <= 0) return TimeSpan.Zero;
            var hours = (y - HeaderHeight) / gridHeight * 24.0;
            return TimeSpan.FromHours(Math.Max(0, Math.Min(24, hours)));
        }

        private static TimeSpan SnapTime(TimeSpan value)
        {
            var ticks = TimeProfileEditorViewModel.Snap.Ticks;
            return TimeSpan.FromTicks((long)Math.Round((double)value.Ticks / ticks) * ticks);
        }

        private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
            value < min ? min : value > max ? max : value;

        private static int CountDays(DayFlags flags) => flags.Days().Count();

        /// <summary>Aligns a coordinate to the device pixel grid so hairlines stay crisp.</summary>
        private static double Snapped(double value) => Math.Round(value) + 0.5;

        private void DrawText(DrawingContext dc, string text, Brush brush, double size, Point at,
            bool centred = false, bool rightAligned = false, double maxWidth = double.PositiveInfinity)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

            if (!double.IsPositiveInfinity(maxWidth))
            {
                formatted.MaxTextWidth = Math.Max(1, maxWidth);
                formatted.MaxLineCount = 1;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
            }

            var origin = at;
            if (centred) origin.X -= formatted.Width / 2;
            if (rightAligned) origin.X -= formatted.Width;
            dc.DrawText(formatted, origin);
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
