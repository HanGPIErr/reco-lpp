using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RecoTool.Services;

namespace RecoTool.Windows
{
    public partial class TodoListSessionWarning : UserControl, INotifyPropertyChanged
    {
        private TodoListSessionTracker _sessionTracker;
        private int _currentTodoId;
        private DispatcherTimer _refreshTimer;
        private DispatcherTimer _notificationClearTimer;
        private ObservableCollection<SessionViewModel> _activeSessions;
        private HashSet<string> _previousUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _refreshRunning;

        public event PropertyChangedEventHandler PropertyChanged;

        public TodoListSessionWarning()
        {
            InitializeComponent();
            DataContext = this;

            _activeSessions = new ObservableCollection<SessionViewModel>();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _refreshTimer.Tick += async (s, e) => await RefreshSessionsAsync();

            _notificationClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _notificationClearTimer.Tick += (s, e) =>
            {
                _notificationClearTimer.Stop();
                NotificationMessage = null;
                HasNotification = false;
            };
        }

        public ObservableCollection<SessionViewModel> ActiveSessions
        {
            get => _activeSessions;
            set { _activeSessions = value; OnPropertyChanged(nameof(ActiveSessions)); }
        }

        private string _summaryMessage;
        public string SummaryMessage
        {
            get => _summaryMessage;
            set { _summaryMessage = value; OnPropertyChanged(nameof(SummaryMessage)); }
        }

        private string _notificationMessage;
        public string NotificationMessage
        {
            get => _notificationMessage;
            set { _notificationMessage = value; OnPropertyChanged(nameof(NotificationMessage)); }
        }

        private bool _hasNotification;
        public bool HasNotification
        {
            get => _hasNotification;
            set { _hasNotification = value; OnPropertyChanged(nameof(HasNotification)); }
        }

        private string _lastRefreshText;
        public string LastRefreshText
        {
            get => _lastRefreshText;
            set { _lastRefreshText = value; OnPropertyChanged(nameof(LastRefreshText)); }
        }

        public async Task InitializeAsync(TodoListSessionTracker sessionTracker, int todoId)
        {
            _sessionTracker = sessionTracker;
            _currentTodoId = todoId;
            _previousUserIds.Clear();

            await RefreshSessionsAsync();

            if (!_refreshTimer.IsEnabled)
                _refreshTimer.Start();
        }

        public async Task LoadSessionsAsync(TodoListSessionTracker sessionTracker, int todoId)
        {
            await InitializeAsync(sessionTracker, todoId);
        }

        public void Stop()
        {
            _refreshTimer?.Stop();
            _notificationClearTimer?.Stop();
        }

        private async Task RefreshSessionsAsync()
        {
            if (_sessionTracker == null || _currentTodoId == 0) return;

            if (System.Threading.Interlocked.Exchange(ref _refreshRunning, 1) == 1)
                return;

            try
            {
                var sessions = await _sessionTracker.GetActiveSessionsAsync(_currentTodoId);
                var active = sessions.Where(s => s.IsActive).ToList();

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Detect joins and leaves
                        var currentIds = new HashSet<string>(
                            active.Select(s => s.UserId ?? ""),
                            StringComparer.OrdinalIgnoreCase);

                        if (_previousUserIds.Count > 0)
                        {
                            var joined = active
                                .Where(s => !_previousUserIds.Contains(s.UserId ?? ""))
                                .Select(s => s.UserName ?? s.UserId)
                                .ToList();
                            var left = _previousUserIds
                                .Where(id => !currentIds.Contains(id))
                                .ToList();

                            if (joined.Count > 0)
                            {
                                NotificationMessage = $"{string.Join(", ", joined)} joined";
                                HasNotification = true;
                                _notificationClearTimer.Stop();
                                _notificationClearTimer.Start();
                            }
                            else if (left.Count > 0)
                            {
                                NotificationMessage = $"{left.Count} user(s) left";
                                HasNotification = true;
                                _notificationClearTimer.Stop();
                                _notificationClearTimer.Start();
                            }
                        }

                        _previousUserIds = currentIds;

                        // Update session list
                        ActiveSessions.Clear();
                        foreach (var s in active)
                            ActiveSessions.Add(new SessionViewModel(s));

                        // Update summary
                        if (active.Count == 0)
                        {
                            SummaryMessage = null;
                            Visibility = Visibility.Collapsed;
                        }
                        else if (active.Count == 1)
                        {
                            var u = active[0].UserName ?? active[0].UserId;
                            SummaryMessage = $"{u} is also viewing this TodoList";
                            Visibility = Visibility.Visible;
                        }
                        else
                        {
                            SummaryMessage = $"{active.Count} other users on this TodoList";
                            Visibility = Visibility.Visible;
                        }

                        LastRefreshText = DateTime.Now.ToString("HH:mm");
                    }
                    catch { }
                }));
            }
            catch { }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _refreshRunning, 0);
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class SessionViewModel
    {
        private readonly TodoSessionInfo _session;

        public SessionViewModel(TodoSessionInfo session)
        {
            _session = session;
        }

        public string UserName => _session.UserName ?? _session.UserId;

        public string StatusText => "viewing";

        public string DurationText
        {
            get
            {
                var duration = _session.Duration;
                if (duration.TotalMinutes < 1) return "(just now)";
                if (duration.TotalMinutes < 60) return $"({(int)duration.TotalMinutes}m)";
                return $"({(int)duration.TotalHours}h)";
            }
        }
    }
}
