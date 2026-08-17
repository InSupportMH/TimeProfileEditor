using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TimeProfileEditor.Model;
using TimeProfileEditor.Services;
using TimeProfileEditor.ViewModels;
using VideoOS.Platform.Client;

namespace TimeProfileEditor.Views
{
    public partial class TimeProfileEditorView : ViewItemWpfUserControl
    {
        private readonly TimeProfileEditorViewModel _viewModel = new TimeProfileEditorViewModel();
        private ScheduleEntry _boundEntry;
        private bool _updatingEditor;
        private bool _syncingDateList;

        public TimeProfileEditorView()
        {
            InitializeComponent();
            DataContext = _viewModel;

            _viewModel.ConfirmDiscard = Confirm;
            _viewModel.ConfirmAction = ConfirmChange;
            _viewModel.ScheduleReplaced += (s, e) =>
            {
                WeekGrid.Refresh();
                MonthGrid.Refresh();
            };
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            WeekGrid.SetSource(_viewModel.WeeklyEntries, _viewModel.ReadOnlyEntries, _viewModel.DateEntries);
            MonthGrid.SetSource(_viewModel.WeeklyEntries, _viewModel.DateEntries, _viewModel.SelectedDates);
            MonthGrid.DateActivated += (s, date) => _viewModel.SelectEntryOn(date);
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            _viewModel.Initialize();
        }

        public override void Init()
        {
        }

        public override void Close()
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            DetachEntry();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(TimeProfileEditorViewModel.SelectedEntry):
                    BindEntryEditor(_viewModel.SelectedEntry);
                    SyncDateListSelection();
                    WeekGrid.Refresh();
                    break;
                case nameof(TimeProfileEditorViewModel.IsEditableProfileOpen):
                    WeekGrid.IsReadOnly = !_viewModel.IsEditableProfileOpen;
                    break;
            }
        }

        private bool Confirm(string message) =>
            MessageBox.Show(Window.GetWindow(this) ?? Application.Current?.MainWindow,
                message, "Osparade ändringar",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

        /// <summary>
        /// Asks before an edit reaches further than the operator pointed at. Defaults to No, so a
        /// stray Enter on the dialog leaves the schedule as it was.
        /// </summary>
        private bool ConfirmChange(string message) =>
            MessageBox.Show(Window.GetWindow(this) ?? Application.Current?.MainWindow,
                message, "Ändringen påverkar mer än de valda datumen",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        // --- Selected entry editor -------------------------------------------------------
        //
        // The day buttons and the two time boxes are driven from code rather than bound: a
        // two-way binding for "is this day part of a bit mask" needs the whole mask to write
        // back, and the time boxes have to stay consistent with each other (moving "Till"
        // before "Från" means the interval runs past midnight, not that it is invalid).

        private void BindEntryEditor(ScheduleEntry entry)
        {
            DetachEntry();
            _boundEntry = entry;
            if (entry != null) entry.PropertyChanged += OnBoundEntryChanged;
            RefreshEntryEditor();
        }

        private void DetachEntry()
        {
            if (_boundEntry != null) _boundEntry.PropertyChanged -= OnBoundEntryChanged;
            _boundEntry = null;
        }

        private void OnBoundEntryChanged(object sender, PropertyChangedEventArgs e) => RefreshEntryEditor();

        private void RefreshEntryEditor()
        {
            if (_updatingEditor) return;
            _updatingEditor = true;
            try
            {
                var entry = _boundEntry;
                var enabled = entry != null && _viewModel.IsEditableProfileOpen;

                foreach (var toggle in DayButtons())
                {
                    var day = (DayOfWeek)Enum.Parse(typeof(DayOfWeek), (string)toggle.Tag);
                    toggle.IsChecked = entry != null && entry.Days.Has(day);
                    toggle.IsEnabled = enabled;
                }

                StartBox.IsEnabled = enabled;
                EndBox.IsEnabled = enabled;

                StartBox.IsEnabled = enabled;
                EndBox.IsEnabled = enabled;
                RangeStartBox.IsEnabled = enabled;
                RangeEndBox.IsEnabled = enabled;
                OpenEndedBox.IsEnabled = enabled;
                DateBox.IsEnabled = enabled;
                DateFromBox.IsEnabled = enabled;
                DateToBox.IsEnabled = enabled;
                AllDayBox.IsEnabled = enabled;

                if (entry == null)
                {
                    StartBox.Text = EndBox.Text = "";
                    RangeStartBox.Text = RangeEndBox.Text = "";
                    DateBox.Text = DateFromBox.Text = DateToBox.Text = "";
                    CrossMidnightHint.Visibility = Visibility.Collapsed;
                    return;
                }

                if (entry.Kind == ScheduleEntryKind.SingleOccurrence)
                {
                    var start = entry.OccurrenceStart ?? DateTime.Today;
                    DateBox.Text = start.ToString("yyyy-MM-dd");
                    DateFromBox.Text = Format(start.TimeOfDay);
                    DateToBox.Text = Format((entry.OccurrenceEnd ?? start.AddHours(1)).TimeOfDay);
                    AllDayBox.IsChecked = entry.AllDayEvent;
                    DateTimeFields.Visibility = entry.AllDayEvent ? Visibility.Collapsed : Visibility.Visible;
                    CrossMidnightHint.Visibility = Visibility.Collapsed;
                    return;
                }

                StartBox.Text = Format(entry.Start);
                EndBox.Text = Format(entry.End);
                CrossMidnightHint.Visibility = entry.CrossesMidnight ? Visibility.Visible : Visibility.Collapsed;

                RangeStartBox.Text = entry.RangeStart.ToString("yyyy-MM-dd");
                RangeEndBox.Text = entry.RangeEnd?.ToString("yyyy-MM-dd") ?? "";
                OpenEndedBox.IsChecked = !entry.RangeEnd.HasValue;
                RangeEndPanel.Visibility = entry.RangeEnd.HasValue ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _updatingEditor = false;
            }
        }

        private ToggleButton[] DayButtons() =>
            new[] { DayMon, DayTue, DayWed, DayThu, DayFri, DaySat, DaySun };

        private static string Format(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}";

        private void OnDayToggled(object sender, RoutedEventArgs e)
        {
            if (_updatingEditor || _boundEntry == null) return;

            var flags = DayFlags.None;
            foreach (var toggle in DayButtons())
            {
                if (toggle.IsChecked != true) continue;
                flags |= ((DayOfWeek)Enum.Parse(typeof(DayOfWeek), (string)toggle.Tag)).ToFlag();
            }

            // An interval on no days would silently stop applying, which never reads as intended.
            if (flags == DayFlags.None)
            {
                RefreshEntryEditor();
                return;
            }

            _boundEntry.Days = flags;
            WeekGrid.Refresh();
        }

        private void OnPresetWeekdays(object sender, RoutedEventArgs e) => ApplyPreset(DayFlags.Weekdays);

        private void OnPresetAll(object sender, RoutedEventArgs e) => ApplyPreset(DayFlags.All);

        private void ApplyPreset(DayFlags flags)
        {
            if (_boundEntry == null || !_viewModel.IsEditableProfileOpen) return;
            _boundEntry.Days = flags;
            WeekGrid.Refresh();
        }

        /// <summary>
        /// Enter moves on rather than committing directly. Every one of these boxes has its own
        /// LostFocus handler, so letting focus move is what routes the value to the right one -
        /// calling a specific commit here would apply the weekly times to a date field.
        /// </summary>
        private void OnTimeBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            (sender as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }

        private void OnTimeBoxCommitted(object sender, RoutedEventArgs e) => CommitTimes();

        private void CommitTimes()
        {
            if (_updatingEditor || _boundEntry == null || !_viewModel.IsEditableProfileOpen) return;

            var start = Parse(StartBox.Text);
            var end = Parse(EndBox.Text);
            if (start == null || end == null)
            {
                RefreshEntryEditor();
                return;
            }

            var startValue = start.Value;
            var endValue = end.Value;

            // "22:00 to 06:00" is a night shift, not a mistake - carry it into the next day.
            if (endValue <= startValue) endValue += TimeSpan.FromHours(24);

            var duration = endValue - startValue;
            if (duration < TimeProfileEditorViewModel.Snap) duration = TimeProfileEditorViewModel.Snap;
            if (duration > TimeProfileRepository.MaxDuration) duration = TimeProfileRepository.MaxDuration;

            _boundEntry.Start = startValue;
            _boundEntry.Duration = duration;

            RefreshEntryEditor();
            WeekGrid.Refresh();
        }

        // --- One-off date fields ---------------------------------------------------------

        private void OnAllDayToggled(object sender, RoutedEventArgs e)
        {
            if (_updatingEditor || _boundEntry == null || !_viewModel.IsEditableProfileOpen) return;

            _boundEntry.AllDayEvent = AllDayBox.IsChecked == true;
            if (_boundEntry.AllDayEvent && _boundEntry.OccurrenceStart.HasValue)
            {
                // The server stores an all-day booking on midnight; keeping the client in step
                // means it does not compare as changed the moment it is saved.
                var day = _boundEntry.OccurrenceStart.Value.Date;
                _boundEntry.OccurrenceStart = day;
                _boundEntry.OccurrenceEnd = day;
            }

            RefreshEntryEditor();
        }

        private void OnDateCommitted(object sender, RoutedEventArgs e)
        {
            if (_updatingEditor || _boundEntry == null || !_viewModel.IsEditableProfileOpen) return;
            if (_boundEntry.Kind != ScheduleEntryKind.SingleOccurrence) return;

            var date = ParseDate(DateBox.Text);
            if (date == null) { RefreshEntryEditor(); return; }

            if (_boundEntry.AllDayEvent)
            {
                _boundEntry.OccurrenceStart = date.Value;
                _boundEntry.OccurrenceEnd = date.Value;
                RefreshEntryEditor();
                return;
            }

            var from = Parse(DateFromBox.Text);
            var to = Parse(DateToBox.Text);
            if (from == null || to == null) { RefreshEntryEditor(); return; }

            var start = date.Value + from.Value;
            var end = date.Value + to.Value;

            // "22:00 to 06:00" on a single date means it runs into the next morning.
            if (end <= start) end = end.AddDays(1);

            _boundEntry.OccurrenceStart = start;
            _boundEntry.OccurrenceEnd = end;
            RefreshEntryEditor();
        }

        // --- Validity range ---------------------------------------------------------------

        private void OnOpenEndedToggled(object sender, RoutedEventArgs e)
        {
            if (_updatingEditor || _boundEntry == null || !_viewModel.IsEditableProfileOpen) return;

            if (OpenEndedBox.IsChecked == true)
            {
                _boundEntry.RangeEnd = null;
            }
            else
            {
                // Default to a year out rather than to the start date, which would make the
                // pattern apply for a single day the moment the box is unticked.
                _boundEntry.RangeEnd = (ParseDate(RangeEndBox.Text) ?? _boundEntry.RangeStart.AddYears(1)).Date;
            }

            RefreshEntryEditor();
        }

        private void OnRangeCommitted(object sender, RoutedEventArgs e)
        {
            if (_updatingEditor || _boundEntry == null || !_viewModel.IsEditableProfileOpen) return;
            if (_boundEntry.Kind != ScheduleEntryKind.Weekly) return;

            var from = ParseDate(RangeStartBox.Text);
            if (from != null) _boundEntry.RangeStart = from.Value;

            if (OpenEndedBox.IsChecked != true)
            {
                var to = ParseDate(RangeEndBox.Text);
                if (to != null)
                    _boundEntry.RangeEnd = to.Value < _boundEntry.RangeStart ? _boundEntry.RangeStart : to.Value;
            }

            RefreshEntryEditor();
            WeekGrid.Refresh();
        }

        private void OnDateListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingDateList) return;
            if (DateList.SelectedItem is ScheduleEntry entry) _viewModel.SelectedEntry = entry;
        }

        /// <summary>
        /// Mirrors the shared selection into the date list. Guarded, because clearing a ListBox's
        /// selection raises SelectionChanged - without the guard, selecting a block in the week grid
        /// would immediately null the selection again on the way back through this handler.
        /// </summary>
        private void SyncDateListSelection()
        {
            _syncingDateList = true;
            try
            {
                var entry = _viewModel.SelectedEntry;
                DateList.SelectedItem = entry != null && _viewModel.DateEntries.Contains(entry) ? entry : null;
            }
            finally
            {
                _syncingDateList = false;
            }
        }

        // --- Help and information panel ---------------------------------------------------

        /// <summary>
        /// Clicking the dimmed area outside the card closes it - the panel reads nothing and
        /// changes nothing, so clicking away from it can only mean "done".
        /// </summary>
        private void OnInfoBackdropClicked(object sender, MouseButtonEventArgs e) =>
            _viewModel.CloseInfoCommand.Execute(null);

        /// <summary>
        /// Stops a click inside the card reaching the backdrop above. Without this, selecting a
        /// line of the help text would close the panel mid-sentence.
        /// </summary>
        private void OnInfoCardClicked(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private static DateTime? ParseDate(string text)
        {
            var value = new DateToStringConverter()
                .ConvertBack(text, typeof(DateTime), null, System.Globalization.CultureInfo.CurrentCulture);
            return value is DateTime date ? date : (DateTime?)null;
        }

        private static TimeSpan? Parse(string text)
        {
            var value = new TimeSpanToStringConverter()
                .ConvertBack(text, typeof(TimeSpan), null, System.Globalization.CultureInfo.CurrentCulture);
            return value is TimeSpan span ? span : (TimeSpan?)null;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // Escape closes the help panel first. It is over everything else and takes no input,
            // so while it is open there is nothing else Escape could reasonably mean.
            if (e.Key == Key.Escape && _viewModel.IsInfoOpen)
            {
                _viewModel.CloseInfoCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Delete || !(e.OriginalSource is WeekScheduleControl)) return;

            if (_viewModel.DeleteEntryCommand.CanExecute(null))
            {
                _viewModel.DeleteEntryCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
