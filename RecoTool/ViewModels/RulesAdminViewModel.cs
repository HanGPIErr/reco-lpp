using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using RecoTool.Services;
using RecoTool.Services.Rules;
using RecoTool.Services.UI;
using RecoTool.UI.Models;

namespace RecoTool.ViewModels
{
    /// <summary>
    /// ViewModel pour <c>RulesAdminWindow.xaml</c>. CRUD sur les règles via
    /// <see cref="TruthTableRepository"/>, recherche / filtrage in-memory,
    /// exécution "Run rules now" sur la grille active.
    ///
    /// <para>
    /// La fenêtre d'édition individuelle (<c>RuleEditorWindow</c>) est lancée
    /// via l'événement <see cref="EditRuleRequested"/> — la View se charge
    /// d'afficher le dialog et de retourner la rule éditée via
    /// <see cref="ApplyEditedRule(TruthRule)"/>.
    /// </para>
    /// </summary>
    public sealed class RulesAdminViewModel : ViewModelBase
    {
        private readonly IRulesAdmin _repo;
        private readonly IDialogService _dialog;
        private List<TruthRule> _allRules = new List<TruthRule>();

        // Backing fields
        private string _searchText;
        private TruthRule _selectedRule;
        private bool _isLoading;
        private string _statusMessage;

        public RulesAdminViewModel(IRulesAdmin repo, IDialogService dialog)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));

            Rules = new ObservableCollection<TruthRule>();
            Scopes = new ObservableCollection<RuleScope>((RuleScope[])Enum.GetValues(typeof(RuleScope)));
            RuleModes = new ObservableCollection<RuleMode>((RuleMode[])Enum.GetValues(typeof(RuleMode)));
            AccountSides = new ObservableCollection<string> { "*", "P", "R" };
            Signs = new ObservableCollection<string> { "*", "C", "D" };
            ApplyTargets = new ObservableCollection<ApplyTarget>((ApplyTarget[])Enum.GetValues(typeof(ApplyTarget)));

            // Bind-compat with RulesAdminWindow.xaml: DataGrid combo cells reach for
            // these collections via {Binding DataContext.X, RelativeSource=...}.
            MtStatusChoices = new ObservableCollection<MtStatusCondition>(
                (MtStatusCondition[])Enum.GetValues(typeof(MtStatusCondition)));
            ActionOptions = new ObservableCollection<OptionItem>();
            KpiOptions = new ObservableCollection<OptionItem>();
            IncidentTypeOptions = new ObservableCollection<OptionItem>();
            ReasonOptions = new ObservableCollection<OptionItem>();

            LoadCommand = new AsyncRelayCommand(ReloadRulesAsync, () => !IsLoading);
            AddCommand = new RelayCommand(() => EditRuleRequested?.Invoke(this, new TruthRule { Enabled = true }));
            EditSelectedCommand = new RelayCommand(
                () => EditRuleRequested?.Invoke(this, SelectedRule?.Clone() ?? new TruthRule()),
                () => SelectedRule != null);
            DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => SelectedRule != null && !IsLoading);
            EnsureTableCommand = new AsyncRelayCommand(EnsureTableAsync, () => !IsLoading);
            RunRulesNowCommand = new RelayCommand(
                () => RunRulesNowRequested?.Invoke(this, EventArgs.Empty),
                () => !IsLoading);
        }

        // ── Properties ──

        public ObservableCollection<TruthRule> Rules { get; }
        public ObservableCollection<RuleScope> Scopes { get; }
        public ObservableCollection<RuleMode> RuleModes { get; }
        public ObservableCollection<string> AccountSides { get; }
        public ObservableCollection<string> Signs { get; }
        public ObservableCollection<ApplyTarget> ApplyTargets { get; }

        /// <summary>Choices for the MT Status DataGrid combo column.</summary>
        public ObservableCollection<MtStatusCondition> MtStatusChoices { get; }
        /// <summary>UserField options for the Action output DataGrid combo column.</summary>
        public ObservableCollection<OptionItem> ActionOptions { get; }
        /// <summary>UserField options for the KPI output DataGrid combo column.</summary>
        public ObservableCollection<OptionItem> KpiOptions { get; }
        /// <summary>UserField options for the Incident Type output DataGrid combo column.</summary>
        public ObservableCollection<OptionItem> IncidentTypeOptions { get; }
        /// <summary>UserField options for the Reason output DataGrid combo column.</summary>
        public ObservableCollection<OptionItem> ReasonOptions { get; }

        public string SearchText
        {
            get => _searchText;
            set { if (SetField(ref _searchText, value)) ApplySearch(); }
        }

        public TruthRule SelectedRule
        {
            get => _selectedRule;
            set => SetField(ref _selectedRule, value);
        }

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

        // ── Commands ──

        public ICommand LoadCommand { get; }
        public ICommand AddCommand { get; }
        public ICommand EditSelectedCommand { get; }
        public ICommand DeleteSelectedCommand { get; }
        public ICommand EnsureTableCommand { get; }
        public ICommand RunRulesNowCommand { get; }

        // ── Events ──

        /// <summary>
        /// Fired with the rule to edit (a draft or a clone of the selected one).
        /// The View opens the editor and, on save, calls <see cref="ApplyEditedRule"/>.
        /// </summary>
        public event EventHandler<TruthRule> EditRuleRequested;

        /// <summary>Fired by RunRulesNow — the View runs the rules engine on the active grid.</summary>
        public event EventHandler RunRulesNowRequested;

        // ── Operations ──

        public async Task ReloadRulesAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = "Loading rules…";
                _allRules = await _repo.LoadRulesAsync().ConfigureAwait(false) ?? new List<TruthRule>();
                ApplySearch();
                StatusMessage = $"{Rules.Count} rule(s)";
            }
            catch (Exception ex)
            {
                StatusMessage = "Error";
                await _dialog.ShowErrorAsync("Load rules", ex.Message).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Called by the View after the editor closes with a saved rule.
        /// Upserts in the repository, then reloads.
        /// </summary>
        public async Task ApplyEditedRule(TruthRule edited)
        {
            if (edited == null) return;
            try
            {
                IsLoading = true;
                StatusMessage = "Saving rule…";
                await _repo.UpsertRuleAsync(edited).ConfigureAwait(false);
                await ReloadRulesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StatusMessage = "Error";
                await _dialog.ShowErrorAsync("Save rule", ex.Message).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task DeleteSelectedAsync()
        {
            var r = SelectedRule;
            if (r == null) return;
            var confirmed = await _dialog.ConfirmAsync("Delete rule",
                $"Delete rule '{r.RuleId ?? "(unnamed)"}' ?").ConfigureAwait(false);
            if (!confirmed) return;

            try
            {
                IsLoading = true;
                await _repo.DeleteRuleAsync(r.RuleId).ConfigureAwait(false);
                await ReloadRulesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _dialog.ShowErrorAsync("Delete rule", ex.Message).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task EnsureTableAsync()
        {
            try
            {
                IsLoading = true;
                await _repo.EnsureRulesTableAsync().ConfigureAwait(false);
                StatusMessage = "Table ensured";
            }
            catch (Exception ex)
            {
                await _dialog.ShowErrorAsync("Ensure table", ex.Message).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void ApplySearch()
        {
            Rules.Clear();
            var src = _allRules.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                var q = _searchText.Trim();
                src = src.Where(r =>
                    (r.RuleId ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Message ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.AccountSide ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.GuaranteeType ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            foreach (var r in src.OrderBy(r => r.Priority).ThenBy(r => r.RuleId))
                Rules.Add(r);
        }
    }
}
