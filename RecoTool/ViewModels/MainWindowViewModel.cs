using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using RecoTool.Infrastructure.Time;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.UI;

namespace RecoTool.ViewModels
{
    /// <summary>
    /// ViewModel for <c>MainWindow.xaml</c>. Pilote of the MVVM migration (Lot 2 of
    /// the testability refactor — see <c>REFACTOR_PLAN_UI_SYNC.md</c>).
    ///
    /// <para>
    /// <b>Scope of this initial version:</b>
    /// </para>
    /// <list type="bullet">
    ///   <item>Country selection (load list, switch, expose CurrentCountry).</item>
    ///   <item>Sync status surface (IsSyncing, LastSyncAt, status string).</item>
    ///   <item>Identity (CurrentUser, computed Title).</item>
    ///   <item>Navigation commands (open import / open reconciliation / refresh / exit).</item>
    /// </list>
    ///
    /// <para>
    /// <b>Out of scope for the pilote:</b> dashboard data binding (HomePage),
    /// reconciliation grid (ReconciliationView), DWINGS popup. Those will move to
    /// dedicated VMs in Lot 3.
    /// </para>
    ///
    /// <para>
    /// <b>Threading:</b> all property setters must be called on the UI thread
    /// (WPF binding contract). Async commands marshal back via the dispatcher
    /// implicitly through <c>await</c>.
    /// </para>
    /// </summary>
    public sealed class MainWindowViewModel : ViewModelBase
    {
        private readonly IOfflineFirstService _offline;
        private readonly IDialogService _dialog;
        private readonly IClock _clock;

        // ── Backing fields ──
        private Country _currentCountry;
        private bool _isSyncing;
        private DateTime? _lastSyncAt;
        private string _syncStatusText;
        private string _currentUser;
        private bool _isBusy;

        public MainWindowViewModel(IOfflineFirstService offline, IDialogService dialog, IClock clock)
        {
            _offline = offline ?? throw new ArgumentNullException(nameof(offline));
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            Countries = new ObservableCollection<Country>();

            // Snapshot identity and current country at construction.
            _currentUser = Environment.UserName ?? "(unknown)";
            _currentCountry = _offline.CurrentCountry;
            _syncStatusText = "Idle";

            // Commands
            LoadCountriesCommand = new AsyncRelayCommand(LoadCountriesAsync);
            SwitchCountryCommand = new AsyncRelayCommand(p => SwitchCountryAsync(p as Country), p => p is Country);
            RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && CurrentCountry != null);
            ImportAmbreCommand = new RelayCommand(() => ImportRequested?.Invoke(this, EventArgs.Empty),
                () => !IsBusy && CurrentCountry != null);
            OpenReconciliationCommand = new RelayCommand(() => OpenReconciliationRequested?.Invoke(this, EventArgs.Empty),
                () => !IsBusy && CurrentCountry != null);
            ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        }

        // ── Read-only collections ──

        /// <summary>List of countries available to the current user. Populated by <see cref="LoadCountriesCommand"/>.</summary>
        public ObservableCollection<Country> Countries { get; }

        /// <summary>Alias of <see cref="Countries"/> exposed for the XAML which binds <c>AvailableCountries</c>.</summary>
        public ObservableCollection<Country> AvailableCountries => Countries;

        // ── Status/UX surface used by MainWindow.xaml bindings ──
        // These are stubs with sane defaults so the XAML can bind without errors.
        // Production code-behind currently sets them via direct field assignment;
        // they will be wired up properly once DataContext = vm is enabled.

        private bool _showFreeAuthButton;
        public bool ShowFreeAuthButton { get => _showFreeAuthButton; set => SetField(ref _showFreeAuthButton, value); }

        // Mirror the global feature flag so the ToggleButton actually drives the
        // multi-user behavior (background sync, snapshot publish, presence tracking).
        // The setter pushes the new value into FeatureFlags.SetMultiUserEnabled so
        // every subscriber (OFS, SyncMonitorService, etc.) reacts as if the legacy
        // code-behind had toggled it. We also notify MultiUserButtonText since it's
        // a computed property — without this, the label stays stale after a click.
        private bool _isMultiUserMode = RecoTool.Configuration.FeatureFlags.ENABLE_MULTI_USER;
        public bool IsMultiUserMode
        {
            get => _isMultiUserMode;
            set
            {
                if (_isMultiUserMode == value) return;
                _isMultiUserMode = value;
                try { RecoTool.Configuration.FeatureFlags.SetMultiUserEnabled(value); } catch { /* best-effort */ }
                OnPropertyChanged(nameof(IsMultiUserMode));
                OnPropertyChanged(nameof(MultiUserButtonText));
            }
        }

        /// <summary>Label shown next to the toggle. Mirrors the legacy code-behind labels (Multi-user / Solo mode).</summary>
        public string MultiUserButtonText => _isMultiUserMode ? "Multi-user" : "Solo mode";

        private object _networkStatusBrush;
        public object NetworkStatusBrush { get => _networkStatusBrush; set => SetField(ref _networkStatusBrush, value); }

        private string _networkStatusText = "Unknown";
        public string NetworkStatusText { get => _networkStatusText; set => SetField(ref _networkStatusText, value); }

        public string AppVersion { get; } =
            typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        private string _initializationStatus = "Initializing…";
        public string InitializationStatus { get => _initializationStatus; set => SetField(ref _initializationStatus, value); }

        private object _initializationBrush;
        public object InitializationBrush { get => _initializationBrush; set => SetField(ref _initializationBrush, value); }

        private string _referentialCacheStatus = "Loading referentials…";
        public string ReferentialCacheStatus { get => _referentialCacheStatus; set => SetField(ref _referentialCacheStatus, value); }

        private object _referentialBrush;
        public object ReferentialBrush { get => _referentialBrush; set => SetField(ref _referentialBrush, value); }

        private bool _referentialCacheAvailable;
        public bool ReferentialCacheAvailable { get => _referentialCacheAvailable; set => SetField(ref _referentialCacheAvailable, value); }

        private string _operationalDataStatus = "Loading operational data…";
        public string OperationalDataStatus { get => _operationalDataStatus; set => SetField(ref _operationalDataStatus, value); }

        private bool _isOffline;
        public bool IsOffline { get => _isOffline; set => SetField(ref _isOffline, value); }

        // ── Properties ──

        public Country CurrentCountry
        {
            get => _currentCountry;
            set { if (SetField(ref _currentCountry, value)) OnPropertyChanged(nameof(Title)); }
        }

        public bool IsSyncing
        {
            get => _isSyncing;
            set => SetField(ref _isSyncing, value);
        }

        public DateTime? LastSyncAt
        {
            get => _lastSyncAt;
            set => SetField(ref _lastSyncAt, value);
        }

        public string SyncStatusText
        {
            get => _syncStatusText;
            set => SetField(ref _syncStatusText, value);
        }

        public string CurrentUser
        {
            get => _currentUser;
            private set => SetField(ref _currentUser, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetField(ref _isBusy, value);
        }

        /// <summary>Computed window title — bound from the View.</summary>
        public string Title
            => CurrentCountry == null
                ? "RecoTool"
                : $"RecoTool — {CurrentCountry.CNT_Name ?? CurrentCountry.CNT_Id}";

        // ── Commands ──

        public ICommand LoadCountriesCommand { get; }
        public ICommand SwitchCountryCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ImportAmbreCommand { get; }
        public ICommand OpenReconciliationCommand { get; }
        public ICommand ExitCommand { get; }

        // ── Events for cross-window navigation ──
        // (View subscribes and shows the appropriate dialog. Keeps the VM ignorant of WPF.)

        public event EventHandler ImportRequested;
        public event EventHandler OpenReconciliationRequested;
        public event EventHandler ExitRequested;

        // ── Operations ──

        private async Task LoadCountriesAsync()
        {
            try
            {
                IsBusy = true;
                var list = await _offline.GetCountries() ?? new List<Country>();
                Countries.Clear();
                foreach (var c in list.OrderBy(c => c.CNT_Name ?? c.CNT_Id))
                    Countries.Add(c);
            }
            catch (Exception ex)
            {
                await _dialog.ShowErrorAsync("Loading countries", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task SwitchCountryAsync(Country target)
        {
            if (target == null || target == CurrentCountry) return;
            try
            {
                IsBusy = true;
                SyncStatusText = $"Switching to {target.CNT_Id}…";
                CurrentCountry = target;
                // Real wiring : OFS does the heavy lifting (paths refresh, referential reload).
                // The VM doesn't care HOW — only that the operation completes or surfaces an error.
                LastSyncAt = _clock.Now;
                SyncStatusText = "Idle";
            }
            catch (Exception ex)
            {
                await _dialog.ShowErrorAsync("Country switch failed", ex.Message);
                SyncStatusText = "Error";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RefreshAsync()
        {
            if (CurrentCountry == null) return;
            try
            {
                IsBusy = true;
                IsSyncing = true;
                SyncStatusText = "Refreshing…";
                // Placeholder: actual sync is driven by OfflineFirstService callers.
                await Task.Yield();
                LastSyncAt = _clock.Now;
                SyncStatusText = "Idle";
            }
            catch (Exception ex)
            {
                await _dialog.ShowErrorAsync("Refresh failed", ex.Message);
                SyncStatusText = "Error";
            }
            finally
            {
                IsSyncing = false;
                IsBusy = false;
            }
        }
    }
}
