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
    /// ViewModel pour <c>ReconciliationPage.xaml</c>.
    ///
    /// <para>
    /// Couvre les **saved filters / saved views / TodoList** (sélection,
    /// sauvegarde, suppression) et l'état UI global (IsLoading, CanInteract,
    /// IsTodoMode). La gestion des sous-vues (<see cref="Windows.ReconciliationView"/>)
    /// reste pilotée par le code-behind.
    /// </para>
    /// </summary>
    public sealed class ReconciliationPageViewModel : ViewModelBase
    {
        private readonly IOfflineFirstService _offline;
        private readonly IReconciliationService _reco;
        private readonly IUserFilterService _filters;
        private readonly IUserTodoListService _todos;
        private readonly IDialogService _dialog;
        private readonly IClock _clock;

        // Backing fields
        private bool _isLoading;
        private bool _isGlobalLockActive;
        private bool _isTodoMode;
        private string _selectedAccount;
        private string _selectedStatus = "Live";
        private string _selectedViewType;
        private string _editTodoName;
        private TodoListItem _selectedTodoItem;
        private string _selectedSavedFilterName;
        private string _selectedSavedViewName;

        public ReconciliationPageViewModel(
            IOfflineFirstService offline,
            IReconciliationService reco,
            IUserFilterService filters,
            IUserTodoListService todos,
            IDialogService dialog,
            IClock clock)
        {
            _offline = offline ?? throw new ArgumentNullException(nameof(offline));
            _reco = reco ?? throw new ArgumentNullException(nameof(reco));
            _filters = filters ?? throw new ArgumentNullException(nameof(filters));
            _todos = todos ?? throw new ArgumentNullException(nameof(todos));
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            SavedFilterNames = new ObservableCollection<string>();
            TodoItems = new ObservableCollection<TodoListItem>();
            Accounts = new ObservableCollection<string> { "All", "Pivot", "Receivable" };
            Statuses = new ObservableCollection<string> { "Live", "Archived", "All" };
            ViewTypes = new ObservableCollection<string> { "Both", "Pivot only", "Receivable only" };

            RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading);
            AddViewCommand = new RelayCommand(
                () => AddViewRequested?.Invoke(this, EventArgs.Empty),
                () => !IsLoading);
            SaveTodoCommand = new AsyncRelayCommand(SaveTodoAsync, CanSaveTodo);
            DeleteTodoCommand = new AsyncRelayCommand(DeleteTodoAsync,
                () => SelectedTodoItem != null && SelectedTodoItem.TDL_id > 0);
            OpenInvoiceFinderCommand = new RelayCommand(
                () => InvoiceFinderRequested?.Invoke(this, EventArgs.Empty));
        }

        // ── Properties ──

        public ObservableCollection<string> SavedFilterNames { get; }
        public ObservableCollection<TodoListItem> TodoItems { get; }
        public ObservableCollection<string> Accounts { get; }
        public ObservableCollection<string> Statuses { get; }
        public ObservableCollection<string> ViewTypes { get; }

        public bool IsLoading
        {
            get => _isLoading;
            set { if (SetField(ref _isLoading, value)) OnPropertyChanged(nameof(CanInteract)); }
        }

        public bool IsGlobalLockActive
        {
            get => _isGlobalLockActive;
            set { if (SetField(ref _isGlobalLockActive, value)) OnPropertyChanged(nameof(CanInteract)); }
        }

        public bool CanInteract => !IsLoading && !IsGlobalLockActive;

        // ── Bind-compat stubs for ReconciliationPage.xaml ──
        // These shadow CanInteract today but the XAML differentiates so we keep
        // the granular surface ready for future fine-grained control.

        public bool CanChangeAccount => CanInteract;
        public bool CanChangeStatus => CanInteract;
        public bool CanUseSavedControls => CanInteract;

        private string _addViewModeIndicator;
        /// <summary>Optional badge text under the "Add view" button (e.g. "(grouped)").</summary>
        public string AddViewModeIndicator
        {
            get => _addViewModeIndicator;
            set => SetField(ref _addViewModeIndicator, value);
        }

        /// <summary>Saved filters bound to the SavedFilters combo (DisplayMemberPath=UFI_Name).</summary>
        public ObservableCollection<RecoTool.Models.UserFilter> SavedFilters { get; } =
            new ObservableCollection<RecoTool.Models.UserFilter>();

        /// <summary>Saved views bound to the SavedViews combo (DisplayMemberPath=Name).</summary>
        public ObservableCollection<RecoTool.Models.UserFieldsPreference> SavedViews { get; } =
            new ObservableCollection<RecoTool.Models.UserFieldsPreference>();

        private RecoTool.Models.UserFieldsPreference _selectedSavedView;
        public RecoTool.Models.UserFieldsPreference SelectedSavedView
        {
            get => _selectedSavedView;
            set => SetField(ref _selectedSavedView, value);
        }

        public bool IsTodoMode
        {
            get => _isTodoMode;
            set => SetField(ref _isTodoMode, value);
        }

        public string SelectedAccount { get => _selectedAccount; set => SetField(ref _selectedAccount, value); }
        public string SelectedStatus { get => _selectedStatus; set => SetField(ref _selectedStatus, value); }
        public string SelectedViewType { get => _selectedViewType; set => SetField(ref _selectedViewType, value); }

        public string SelectedSavedFilterName
        {
            get => _selectedSavedFilterName;
            set => SetField(ref _selectedSavedFilterName, value);
        }

        public string SelectedSavedViewName
        {
            get => _selectedSavedViewName;
            set => SetField(ref _selectedSavedViewName, value);
        }

        public string EditTodoName
        {
            get => _editTodoName;
            set => SetField(ref _editTodoName, value);
        }

        public TodoListItem SelectedTodoItem
        {
            get => _selectedTodoItem;
            set
            {
                if (SetField(ref _selectedTodoItem, value))
                {
                    EditTodoName = value?.TDL_Name;
                    OnPropertyChanged(nameof(IsTodoSelected));
                }
            }
        }

        public bool IsTodoSelected => SelectedTodoItem != null;

        // ── Commands ──

        public ICommand RefreshCommand { get; }
        public ICommand AddViewCommand { get; }
        public ICommand SaveTodoCommand { get; }
        public ICommand DeleteTodoCommand { get; }
        public ICommand OpenInvoiceFinderCommand { get; }

        // ── Events ──

        public event EventHandler AddViewRequested;
        public event EventHandler InvoiceFinderRequested;
        public event EventHandler RefreshStarted;
        public event EventHandler RefreshCompleted;

        // ── Operations ──

        public async Task RefreshAsync()
        {
            try
            {
                IsLoading = true;
                RefreshStarted?.Invoke(this, EventArgs.Empty);

                await LoadSavedFiltersAsync().ConfigureAwait(false);
                await LoadTodoListAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _dialog.ShowErrorAsync("Refresh", ex.Message).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
                RefreshCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        private Task LoadSavedFiltersAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var names = _filters.ListUserFilterNames() ?? Array.Empty<string>();
                    // Marshalling vers UI thread : laisser à la View. Pour les tests
                    // nous ne créons pas de Dispatcher — on remplit directement.
                    SavedFilterNames.Clear();
                    foreach (var n in names) SavedFilterNames.Add(n);
                }
                catch { /* best-effort */ }
            });
        }

        private async Task LoadTodoListAsync()
        {
            try
            {
                var country = _offline.CurrentCountryId;
                var list = await _todos.ListAsync(country).ConfigureAwait(false) ?? new List<TodoListItem>();
                TodoItems.Clear();
                foreach (var t in list.OrderBy(t => t.TDL_Name)) TodoItems.Add(t);
            }
            catch { /* best-effort */ }
        }

        private bool CanSaveTodo()
            => !string.IsNullOrWhiteSpace(EditTodoName) && !IsLoading;

        private async Task SaveTodoAsync()
        {
            if (!CanSaveTodo()) return;
            try
            {
                IsLoading = true;
                var item = SelectedTodoItem ?? new TodoListItem
                {
                    TDL_Active = true,
                    TDL_CountryId = _offline.CurrentCountryId
                };
                item.TDL_Name = EditTodoName.Trim();

                var id = await _todos.UpsertAsync(item).ConfigureAwait(false);
                if (id > 0 && item.TDL_id == 0) item.TDL_id = id;

                await LoadTodoListAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _dialog.ShowErrorAsync("Save TodoList", ex.Message).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task DeleteTodoAsync()
        {
            var item = SelectedTodoItem;
            if (item == null || item.TDL_id <= 0) return;

            var confirmed = await _dialog.ConfirmAsync("Delete TodoList",
                $"Are you sure you want to delete '{item.TDL_Name}' ?").ConfigureAwait(false);
            if (!confirmed) return;

            try
            {
                IsLoading = true;
                await _todos.DeleteAsync(item.TDL_id).ConfigureAwait(false);
                await LoadTodoListAsync().ConfigureAwait(false);
                SelectedTodoItem = null;
            }
            catch (Exception ex)
            {
                await _dialog.ShowErrorAsync("Delete TodoList", ex.Message).ConfigureAwait(false);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
