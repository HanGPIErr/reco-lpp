using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RecoTool.Domain.Repositories;
using RecoTool.Infrastructure.Time;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.UI;

namespace RecoTool.ViewModels
{
    /// <summary>
    /// ViewModel pour <c>HomePage.xaml</c> (dashboard de démarrage).
    ///
    /// <para>
    /// <b>Scope du squelette initial</b> (Lot 3 — pilote suivant après MainWindow) :
    /// </para>
    /// <list type="bullet">
    ///   <item>État global (IsLoading, StatusMessage, LastUpdateTime).</item>
    ///   <item>Liste des pays + sélection courante.</item>
    ///   <item>KPIs principaux (counts par catégorie d'incident, totals).</item>
    ///   <item>Commands : Refresh, ImportAmbre, OpenReports, ExportDailyKpi, OpenTodoCard.</item>
    ///   <item>Événement <see cref="RefreshCompleted"/> pour que MainWindow notifie d'autres pages.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Hors scope du pilote</b> : LiveCharts series, TodoCards multi-utilisateur,
    /// DWINGS warning timer, leaderboard. Ces éléments sont à porter dans un second
    /// temps une fois que la base du VM est validée. Le code-behind existant
    /// continue de gérer ces parties via la View pour l'instant.
    /// </para>
    /// </summary>
    public sealed class HomePageViewModel : ViewModelBase
    {
        private readonly IOfflineFirstService _offline;
        private readonly IReconciliationService _reco;
        private readonly IDialogService _dialog;
        private readonly IClock _clock;
        private readonly IDataAmbreRepository _ambreRepo;

        // ── Backing fields ──
        private bool _isLoading;
        private string _statusMessage = "Idle";
        private DateTime? _lastUpdateTime;
        private Country _currentCountry;

        // KPIs
        private int _missingInvoicesCount;
        private int _paidButNotReconciledCount;
        private int _underInvestigationCount;
        private decimal _totalReceivableAmount;
        private decimal _totalPivotAmount;
        private int _receivableAccountsCount;
        private int _pivotAccountsCount;
        private int _totalLiveCount;
        private int _totalToReviewCount;
        private int _reviewedTodayCount;
        private double _matchedPercentage;

        public HomePageViewModel(
            IOfflineFirstService offline,
            IReconciliationService reco,
            IDialogService dialog,
            IClock clock,
            IDataAmbreRepository ambreRepo = null)
        {
            _offline = offline ?? throw new ArgumentNullException(nameof(offline));
            _reco = reco ?? throw new ArgumentNullException(nameof(reco));
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _ambreRepo = ambreRepo; // Optional — falls back to _reco.GetAmbreDataAsync when null.

            AvailableCountries = new ObservableCollection<Country>();
            TodoCardCounts = new ObservableCollection<TodoCardCount>();

            _currentCountry = _offline.CurrentCountry;

            RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading);
            ImportAmbreCommand = new RelayCommand(
                () => ImportRequested?.Invoke(this, EventArgs.Empty),
                () => !IsLoading && CurrentCountry != null);
            OpenReportsCommand = new RelayCommand(
                () => ReportsRequested?.Invoke(this, EventArgs.Empty),
                () => !IsLoading && CurrentCountry != null);
            ExportDailyKpiCommand = new RelayCommand(
                () => ExportKpiRequested?.Invoke(this, EventArgs.Empty),
                () => !IsLoading && CurrentCountry != null);
            OpenTodoCardCommand = new RelayCommand(
                p => TodoCardOpened?.Invoke(this, p as TodoCardCount),
                p => !IsLoading && p is TodoCardCount);
        }

        // ── Read-only collections ──

        public ObservableCollection<Country> AvailableCountries { get; }
        public ObservableCollection<TodoCardCount> TodoCardCounts { get; }

        // ── Properties ──

        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        public DateTime? LastUpdateTime
        {
            get => _lastUpdateTime;
            set => SetField(ref _lastUpdateTime, value);
        }

        public Country CurrentCountry
        {
            get => _currentCountry;
            set { if (SetField(ref _currentCountry, value)) OnPropertyChanged(nameof(CurrentCountryName)); }
        }

        public string CurrentCountryName
            => CurrentCountry?.CNT_Name ?? CurrentCountry?.CNT_Id ?? "(none)";

        public string CurrentCountryId => CurrentCountry?.CNT_Id;

        // ── KPIs ──

        public int MissingInvoicesCount { get => _missingInvoicesCount; set => SetField(ref _missingInvoicesCount, value); }
        public int PaidButNotReconciledCount { get => _paidButNotReconciledCount; set => SetField(ref _paidButNotReconciledCount, value); }
        public int UnderInvestigationCount { get => _underInvestigationCount; set => SetField(ref _underInvestigationCount, value); }

        public decimal TotalReceivableAmount { get => _totalReceivableAmount; set => SetField(ref _totalReceivableAmount, value); }
        public decimal TotalPivotAmount { get => _totalPivotAmount; set => SetField(ref _totalPivotAmount, value); }

        public int ReceivableAccountsCount { get => _receivableAccountsCount; set => SetField(ref _receivableAccountsCount, value); }
        public int PivotAccountsCount { get => _pivotAccountsCount; set => SetField(ref _pivotAccountsCount, value); }

        public int TotalLiveCount { get => _totalLiveCount; set => SetField(ref _totalLiveCount, value); }
        public int TotalToReviewCount { get => _totalToReviewCount; set => SetField(ref _totalToReviewCount, value); }
        public int ReviewedTodayCount { get => _reviewedTodayCount; set => SetField(ref _reviewedTodayCount, value); }
        public double MatchedPercentage { get => _matchedPercentage; set => SetField(ref _matchedPercentage, value); }

        // ── Bind-compat stubs for HomePage.xaml ──
        // These collections and properties are referenced by the existing XAML;
        // they're stubs with sensible defaults so the bindings resolve. Wiring to
        // real data sources happens incrementally in the code-behind today.

        private bool _isDwingsDataFromToday = true;
        public bool IsDwingsDataFromToday { get => _isDwingsDataFromToday; set => SetField(ref _isDwingsDataFromToday, value); }

        private string _dwingsWarningMessage;
        public string DwingsWarningMessage { get => _dwingsWarningMessage; set => SetField(ref _dwingsWarningMessage, value); }

        /// <summary>Cards on the home page header (active todos with badges).</summary>
        public ObservableCollection<TodoCard> TodoCards { get; } = new ObservableCollection<TodoCard>();
        /// <summary>Alerts shown in the right-hand alert strip.</summary>
        public ObservableCollection<HomeAlert> AlertItems { get; } = new ObservableCollection<HomeAlert>();
        /// <summary>Top-N assignees by review throughput this week.</summary>
        public ObservableCollection<AssigneeStats> AssigneeLeaderboard { get; } = new ObservableCollection<AssigneeStats>();

        /// <summary>Aggregated completion estimate (unreviewed count + daily rate + ETA).</summary>
        public CompletionEstimate CompletionEstimate { get; set; } = new CompletionEstimate();

        // Charts. Object types so the VM doesn't take a hard reference to LiveCharts here.
        public object NewDeletedDailySeries { get; set; }
        public object NewDeletedDailyLabels { get; set; }
        public object DeletionDelaySeries { get; set; }
        public object DeletionDelayLabels { get; set; }
        public object ReceivablePivotByCurrencySeries { get; set; }
        public object ReceivablePivotByCurrencyLabels { get; set; }
        public object CurrencyDistributionSeries { get; set; }
        public object ActionDistributionSeries { get; set; }
        public object ActionLabels { get; set; }
        public object ReviewTrendSeries { get; set; }
        public object ReviewTrendLabels { get; set; }
        public object MatchedRateTrendSeries { get; set; }
        public object MatchedRateTrendLabels { get; set; }

        // ── Commands ──

        public ICommand RefreshCommand { get; }
        public ICommand ImportAmbreCommand { get; }
        public ICommand OpenReportsCommand { get; }
        public ICommand ExportDailyKpiCommand { get; }
        public ICommand OpenTodoCardCommand { get; }

        // ── Events for cross-window navigation ──

        public event EventHandler ImportRequested;
        public event EventHandler ReportsRequested;
        public event EventHandler ExportKpiRequested;
        public event EventHandler<TodoCardCount> TodoCardOpened;
        public event EventHandler RefreshStarted;
        public event EventHandler RefreshCompleted;

        // ── Operations ──

        /// <summary>
        /// Recharge la liste des pays et les KPIs du pays courant.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (CurrentCountry == null)
            {
                _currentCountry = _offline.CurrentCountry;
                OnPropertyChanged(nameof(CurrentCountry));
                OnPropertyChanged(nameof(CurrentCountryName));
                OnPropertyChanged(nameof(CurrentCountryId));
            }

            try
            {
                IsLoading = true;
                StatusMessage = "Loading…";
                RefreshStarted?.Invoke(this, EventArgs.Empty);

                await LoadCountriesAsync().ConfigureAwait(false);
                if (CurrentCountry != null)
                    await LoadKpisAsync(CurrentCountry.CNT_Id).ConfigureAwait(false);

                LastUpdateTime = _clock.Now;
                StatusMessage = "Idle";
            }
            catch (Exception ex)
            {
                StatusMessage = "Error";
                await _dialog.ShowErrorAsync("Dashboard refresh", ex.Message).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
                RefreshCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task LoadCountriesAsync()
        {
            var list = await _offline.GetCountries().ConfigureAwait(false) ?? new List<Country>();
            AvailableCountries.Clear();
            foreach (var c in list.OrderBy(c => c.CNT_Name ?? c.CNT_Id))
                AvailableCountries.Add(c);
        }

        /// <summary>
        /// Calcule les KPIs principaux à partir des données AMBRE. Prefers
        /// <see cref="IDataAmbreRepository.GetAllAsync"/> when injected (the new
        /// repository abstraction); falls back to <see cref="IReconciliationService.GetAmbreDataAsync"/>
        /// otherwise — backward-compatible with existing call sites and test setups.
        /// Ne fait pas d'agrégation côté DB (on prend la liste complète et on agrège
        /// en mémoire) — suffisant pour des volumes typiques (~20k lignes).
        /// </summary>
        public async Task LoadKpisAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return;

            IReadOnlyList<DataAmbre> rows;
            if (_ambreRepo != null)
            {
                rows = await _ambreRepo.GetAllAsync(countryId, includeDeleted: false, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                // Fallback: legacy IReconciliationService path — List<T> implements IReadOnlyList<T>.
                rows = await _reco.GetAmbreDataAsync(countryId, includeDeleted: false).ConfigureAwait(false);
            }
            rows = rows ?? (IReadOnlyList<DataAmbre>)Array.Empty<DataAmbre>();

            TotalLiveCount = rows.Count;

            var country = CurrentCountry;
            var pivotCode = country?.CNT_AmbrePivot;
            var receivableCode = country?.CNT_AmbreReceivable;

            var pivotLines = rows.Where(r => string.Equals(r.Account_ID, pivotCode, StringComparison.OrdinalIgnoreCase)).ToList();
            var receivableLines = rows.Where(r => string.Equals(r.Account_ID, receivableCode, StringComparison.OrdinalIgnoreCase)).ToList();

            PivotAccountsCount = pivotLines.Count;
            ReceivableAccountsCount = receivableLines.Count;
            TotalPivotAmount = pivotLines.Sum(r => r.SignedAmount);
            TotalReceivableAmount = receivableLines.Sum(r => r.SignedAmount);
        }
    }

    /// <summary>
    /// Lightweight DTO used by <see cref="HomePageViewModel.TodoCardCounts"/> bindings.
    /// </summary>
    public sealed class TodoCardCount
    {
        public string Title { get; set; }
        public int Count { get; set; }
        public string FilterPreset { get; set; }
    }

    /// <summary>
    /// Per-card surface for the home page todo strip — extends TodoCardCount with
    /// the UX fields used by HomePage.xaml (counts, badges, formatted amounts).
    /// </summary>
    public sealed class TodoCard
    {
        public RecoTool.Models.TodoListItem Item { get; set; }
        public int NewCount { get; set; }
        public int NotLinkedCount { get; set; }
        public int Count { get; set; }
        public int ReviewedCount { get; set; }
        public decimal ActualTotal { get; set; }
        public string AmountsText { get; set; }

        // Active-users badge state
        public bool HasActiveUsers { get; set; }
        public object ActiveUsersBadgeBackground { get; set; }
        public string ActiveUsersTooltip { get; set; }
        public string ActiveUsersText { get; set; }
    }

    /// <summary>Alert strip item for HomePage.xaml.</summary>
    public sealed class HomeAlert
    {
        public string Type { get; set; }     // "Warning" / "Info" / "Critical"
        public string Title { get; set; }
        public string Message { get; set; }
        public int Count { get; set; }
    }

    /// <summary>Assignee row in the leaderboard.</summary>
    public sealed class AssigneeStats
    {
        public string Assignee { get; set; }
        public int ReviewedThisWeek { get; set; }
        public double CompletionRate { get; set; }
    }

    /// <summary>Aggregated completion-estimate bound from HomePage.xaml.</summary>
    public sealed class CompletionEstimate
    {
        public int UnreviewedCount { get; set; }
        public double DailyReviewRate { get; set; }
        public int EstimatedDaysToComplete { get; set; }
        public double CompletionPercentage { get; set; }
    }
}
