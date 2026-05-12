using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RecoTool.Domain.Repositories;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.DTOs;
using RecoTool.Services.UI;
using RecoTool.UI.Models;

namespace RecoTool.ViewModels
{
    /// <summary>
    /// ViewModel pour <c>ReconciliationDetailWindow.xaml</c>. Encapsule l'état
    /// d'édition d'une ligne de rapprochement + le lien avec une invoice DWINGS.
    ///
    /// <para>
    /// Scope V1 :
    /// </para>
    /// <list type="bullet">
    ///   <item>Hydrate les champs depuis la <see cref="Reconciliation"/> existante.</item>
    ///   <item>Expose Action/KPI/IncidentType/Reason via <see cref="ObservableCollection{T}"/>.</item>
    ///   <item>Save / Cancel / Unlink commands.</item>
    ///   <item>NotInDwings command — applique les flags Action=NA + KPI=NotTFSC.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Hors scope</b> : recherche DWINGS interactive, suggestion auto, grille de
    /// résultats. Ces éléments restent côté code-behind dans la première vague.
    /// </para>
    /// </summary>
    public sealed class ReconciliationDetailViewModel : ViewModelBase
    {
        private readonly IReconciliationService _reco;
        private readonly IOfflineFirstService _offline;
        private readonly IDialogService _dialog;
        // Optional T_Ref_User_Fields repository (Vague 7 consumer migration).
        // When non-null, the VM prefers reading from the repository over the legacy
        // _offline.UserFields cached property. Stays null on the existing ctor
        // overload to keep callers/tests behaving exactly as before.
        private readonly IUserFieldsRepository _userFieldsRepo;

        // Bound row + reconciliation
        private readonly ReconciliationViewData _row;
        private Reconciliation _reconciliation;

        // Backing fields
        private int? _selectedActionId;
        private int? _selectedKpiId;
        private int? _selectedIncidentTypeId;
        private int? _selectedReasonId;
        private string _comments;
        private bool _isLoading;
        private bool _hasUnsavedChanges;
        private string _statusMessage;
        private DateTime? _firstClaimDate;

        public ReconciliationDetailViewModel(
            ReconciliationViewData row,
            IReconciliationService reco,
            IOfflineFirstService offline,
            IDialogService dialog,
            IUserFieldsRepository userFieldsRepo = null)
        {
            _row = row ?? throw new ArgumentNullException(nameof(row));
            _reco = reco ?? throw new ArgumentNullException(nameof(reco));
            _offline = offline ?? throw new ArgumentNullException(nameof(offline));
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _userFieldsRepo = userFieldsRepo; // optional — may be null in legacy/test paths

            ActionOptions = new ObservableCollection<OptionItem>();
            KPIOptions = new ObservableCollection<OptionItem>();
            IncidentTypeOptions = new ObservableCollection<OptionItem>();
            ReasonOptions = new ObservableCollection<OptionItem>();

            LoadCommand = new AsyncRelayCommand(LoadAsync, () => !_isLoading);
            SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasUnsavedChanges && !IsLoading);
            CancelCommand = new RelayCommand(() => CancelRequested?.Invoke(this, EventArgs.Empty));
            UnlinkCommand = new AsyncRelayCommand(UnlinkAsync, () => HasDwingsLink && !IsLoading);
            NotInDwingsCommand = new AsyncRelayCommand(MarkNotInDwingsAsync, () => !IsLoading);

            PopulateOptionsFromOffline();
        }

        // ── Properties ──

        public ObservableCollection<OptionItem> ActionOptions { get; }
        public ObservableCollection<OptionItem> KPIOptions { get; }
        public ObservableCollection<OptionItem> IncidentTypeOptions { get; }
        public ObservableCollection<OptionItem> ReasonOptions { get; }

        public string Title => $"Detail — {_row.Reconciliation_Num ?? _row.ID}";

        public ReconciliationViewData Row => _row;
        public Reconciliation Reconciliation => _reconciliation;

        public int? SelectedActionId { get => _selectedActionId; set => SetField(ref _selectedActionId, value, markDirty: true); }
        public int? SelectedKPIId { get => _selectedKpiId; set => SetField(ref _selectedKpiId, value, markDirty: true); }
        public int? SelectedIncidentTypeId { get => _selectedIncidentTypeId; set => SetField(ref _selectedIncidentTypeId, value, markDirty: true); }
        public int? SelectedReasonId { get => _selectedReasonId; set => SetField(ref _selectedReasonId, value, markDirty: true); }
        public string Comments { get => _comments; set => SetField(ref _comments, value, markDirty: true); }
        public DateTime? FirstClaimDate { get => _firstClaimDate; set => SetField(ref _firstClaimDate, value, markDirty: true); }

        public bool IsLoading { get => _isLoading; set => SetField(ref _isLoading, value); }
        public bool HasUnsavedChanges { get => _hasUnsavedChanges; set => SetField(ref _hasUnsavedChanges, value); }
        public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

        public bool HasDwingsLink => !string.IsNullOrWhiteSpace(_reconciliation?.DWINGS_InvoiceID)
                                  || !string.IsNullOrWhiteSpace(_reconciliation?.DWINGS_GuaranteeID)
                                  || !string.IsNullOrWhiteSpace(_reconciliation?.DWINGS_BGPMT);

        // ── Commands ──

        public ICommand LoadCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand UnlinkCommand { get; }
        public ICommand NotInDwingsCommand { get; }

        // ── Events ──

        public event EventHandler<bool> SaveCompleted; // arg = success
        public event EventHandler CancelRequested;

        // ── Operations ──

        public async Task LoadAsync()
        {
            try
            {
                IsLoading = true;
                _reconciliation = await _reco.GetOrCreateReconciliationAsync(_row.ID).ConfigureAwait(false);
                if (_reconciliation != null)
                {
                    _selectedActionId = _reconciliation.Action;
                    _selectedKpiId = _reconciliation.KPI;
                    _selectedIncidentTypeId = _reconciliation.IncidentType;
                    _selectedReasonId = _reconciliation.ReasonNonRisky;
                    _comments = _reconciliation.Comments;
                    _firstClaimDate = _reconciliation.FirstClaimDate;
                    // Notif group (Action/KPI/Incident/Reason/Comments/FirstClaimDate + HasDwingsLink)
                    OnPropertyChanged(nameof(SelectedActionId));
                    OnPropertyChanged(nameof(SelectedKPIId));
                    OnPropertyChanged(nameof(SelectedIncidentTypeId));
                    OnPropertyChanged(nameof(SelectedReasonId));
                    OnPropertyChanged(nameof(Comments));
                    OnPropertyChanged(nameof(FirstClaimDate));
                    OnPropertyChanged(nameof(HasDwingsLink));
                }
                HasUnsavedChanges = false;
            }
            catch (Exception ex)
            {
                await _dialog.ShowErrorAsync("Load reconciliation", ex.Message).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task SaveAsync()
        {
            if (_reconciliation == null) return;
            try
            {
                IsLoading = true;
                StatusMessage = "Saving…";
                _reconciliation.Action = _selectedActionId;
                _reconciliation.KPI = _selectedKpiId;
                _reconciliation.IncidentType = _selectedIncidentTypeId;
                _reconciliation.ReasonNonRisky = _selectedReasonId;
                _reconciliation.Comments = _comments;
                _reconciliation.FirstClaimDate = _firstClaimDate;

                var ok = await _reco.SaveReconciliationsAsync(new[] { _reconciliation }).ConfigureAwait(false);
                StatusMessage = ok ? "Saved" : "Save failed";
                HasUnsavedChanges = !ok;
                SaveCompleted?.Invoke(this, ok);
            }
            catch (Exception ex)
            {
                StatusMessage = "Error";
                await _dialog.ShowErrorAsync("Save", ex.Message).ConfigureAwait(false);
                SaveCompleted?.Invoke(this, false);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task UnlinkAsync()
        {
            if (_reconciliation == null) return;
            var confirmed = await _dialog.ConfirmAsync("Unlink DWINGS",
                "Are you sure you want to remove the DWINGS link from this reconciliation?")
                .ConfigureAwait(false);
            if (!confirmed) return;

            _reconciliation.DWINGS_InvoiceID = null;
            _reconciliation.DWINGS_GuaranteeID = null;
            _reconciliation.DWINGS_BGPMT = null;
            HasUnsavedChanges = true;
            OnPropertyChanged(nameof(HasDwingsLink));
            await SaveAsync().ConfigureAwait(false);
        }

        public async Task MarkNotInDwingsAsync()
        {
            if (_reconciliation == null) await LoadAsync().ConfigureAwait(false);
            if (_reconciliation == null) return;

            // Looks up Action=NA and KPI=NotTFSC from user fields.
            // Vague 7 migration: prefer IUserFieldsRepository when injected, with graceful
            // fallback to the legacy _offline.UserFields cached property.
            IEnumerable<UserField> fields = null;
            if (_userFieldsRepo != null)
            {
                try
                {
                    fields = await _userFieldsRepo.GetAllAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    fields = null;
                }
            }
            if (fields == null) fields = _offline.UserFields ?? new List<UserField>();
            var na = fields.FirstOrDefault(uf =>
                string.Equals(uf.USR_Category, "Action", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(uf.USR_FieldName, "N/A", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(uf.USR_FieldName, "NA", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(uf.USR_FieldName, "Not Applicable", StringComparison.OrdinalIgnoreCase)));
            var notTfsc = fields.FirstOrDefault(uf =>
                string.Equals(uf.USR_Category, "KPI", StringComparison.OrdinalIgnoreCase)
                && string.Equals(uf.USR_FieldName, "Not TFSC", StringComparison.OrdinalIgnoreCase));

            if (na != null) SelectedActionId = na.USR_ID;
            if (notTfsc != null) SelectedKPIId = notTfsc.USR_ID;
            Comments = AppendComment(Comments, "Marked as NOT IN DWINGS");
            await SaveAsync().ConfigureAwait(false);
        }

        // ── Helpers ──

        private void PopulateOptionsFromOffline()
        {
            // Vague 7 migration: prefer IUserFieldsRepository (T_Ref_User_Fields) when
            // injected. Falls back to the legacy _offline.UserFields cached property
            // on any failure (or when no repo was supplied) so behaviour is preserved
            // bit-for-bit on the existing 4-arg ctor used by tests + RulesAdminWindow
            // / ReconciliationDetailWindow code-behind.
            //
            // PopulateOptionsFromOffline is invoked from the ctor (sync). The repo
            // call is bridged synchronously; in practice the repo loads ~50 rows once
            // and is fronted by a cache, so blocking on this is acceptable. On any
            // exception we silently fall back to the cached property.
            IEnumerable<UserField> fields = null;
            if (_userFieldsRepo != null)
            {
                try
                {
                    fields = _userFieldsRepo.GetAllAsync(CancellationToken.None)
                                            .ConfigureAwait(false)
                                            .GetAwaiter()
                                            .GetResult();
                }
                catch
                {
                    fields = null; // fall through to legacy path
                }
            }
            if (fields == null) fields = _offline?.UserFields ?? new List<UserField>();

            FillOptions(ActionOptions, fields.Where(f => string.Equals(f.USR_Category, "Action", StringComparison.OrdinalIgnoreCase)));
            FillOptions(KPIOptions, fields.Where(f => string.Equals(f.USR_Category, "KPI", StringComparison.OrdinalIgnoreCase)));
            FillOptions(IncidentTypeOptions, fields.Where(f =>
                string.Equals(f.USR_Category, "INC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.USR_Category, "Incident Type", StringComparison.OrdinalIgnoreCase)));
            FillOptions(ReasonOptions, fields.Where(f =>
                string.Equals(f.USR_Category, "Reason", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.USR_Category, "ReasonNonRisky", StringComparison.OrdinalIgnoreCase)));
        }

        private static void FillOptions(ObservableCollection<OptionItem> col, IEnumerable<UserField> source)
        {
            col.Clear();
            foreach (var f in source.OrderBy(f => f.USR_FieldName))
                col.Add(new OptionItem { Id = f.USR_ID, Name = f.USR_FieldName ?? f.USR_FieldDescription ?? $"#{f.USR_ID}" });
        }

        private string AppendComment(string existing, string newLine)
        {
            if (string.IsNullOrWhiteSpace(newLine)) return existing;
            // Use the static BaseEntity clock so this VM remains free of an IClock ctor
            // dependency. Tests can swap BaseEntity.Clock or set the clock at module init.
            var clock = RecoTool.Models.BaseEntity.Clock;
            var stamp = $"[{clock.Now:yyyy-MM-dd HH:mm}] " + newLine;
            return string.IsNullOrWhiteSpace(existing) ? stamp : (stamp + Environment.NewLine + existing);
        }

        private bool SetField<T>(ref T field, T value, bool markDirty, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            if (markDirty) HasUnsavedChanges = true;
            return true;
        }
    }

    // OptionItem moved/canonicalised : use RecoTool.UI.Models.OptionItem instead.
    // (The previous duplicate definition in RecoTool.ViewModels caused CS0104
    // ambiguity when both namespaces were imported in WPF code-behind.)

    /// <summary>
    /// Unified row exposed in the "Linked items" DataGrid of <c>ReconciliationDetailWindow.xaml</c>.
    /// Property names match the XAML DataGrid column bindings.
    /// </summary>
    public sealed class LinkedItemRow
    {
        public string Type { get; set; }         // "Invoice" / "Guarantee"
        public string Id { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }       // GENERATED / PAID / OPEN / ...
        public decimal? Amount { get; set; }
        public string Currency { get; set; }
        public string BGPMT { get; set; }
        public string BusinessCase { get; set; }
    }
}
