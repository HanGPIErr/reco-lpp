using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RecoTool.Domain.Repositories;
using RecoTool.UI.Models;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.DTOs;

namespace RecoTool.Windows
{
    // Partial: Options (collections + loaders) for ReconciliationView
    public partial class ReconciliationView
    {
        // Thin wrapper for cached option lists
        private OptionsService _optionsService;
        // Repository for T_Ref_User_Fields (Vague 6 first consumer migration).
        // Lazily resolved from App.ServiceProvider in EnsureUserFieldsRepository(),
        // because the DI ctor lives in another partial (ReconciliationView.xaml.cs)
        // which the migration spec forbids us from touching.
        private IUserFieldsRepository _userFieldsRepo;
        private bool _userFieldsRepoResolved;
        // Options for referential ComboBoxes
        private ObservableCollection<OptionItem> _actionOptions = new ObservableCollection<OptionItem>();
        private ObservableCollection<OptionItem> _kpiOptions = new ObservableCollection<OptionItem>();
        private ObservableCollection<OptionItem> _incidentTypeOptions = new ObservableCollection<OptionItem>();
        public ObservableCollection<OptionItem> ActionOptions { get => _actionOptions; private set { _actionOptions = value; OnPropertyChanged(nameof(ActionOptions)); } }
        public ObservableCollection<OptionItem> KpiOptions { get => _kpiOptions; private set { _kpiOptions = value; OnPropertyChanged(nameof(KpiOptions)); } }
        public ObservableCollection<OptionItem> IncidentTypeOptions { get => _incidentTypeOptions; private set { _incidentTypeOptions = value; OnPropertyChanged(nameof(IncidentTypeOptions)); } }

        // Options for Assignee ComboBox (users from T_User)
        private ObservableCollection<UserOption> _assigneeOptions = new ObservableCollection<UserOption>();
        public ObservableCollection<UserOption> AssigneeOptions
        {
            get => _assigneeOptions;
            private set { _assigneeOptions = value; OnPropertyChanged(nameof(AssigneeOptions)); }
        }

        // Dynamic options for filter ComboBoxes (Currency / Guarantee Type / Guarantee Status)
        private ObservableCollection<string> _currencyOptions = new ObservableCollection<string>();
        private ObservableCollection<string> _guaranteeTypeOptions = new ObservableCollection<string>();
        private ObservableCollection<string> _guaranteeStatusOptions = new ObservableCollection<string>();
        public ObservableCollection<string> CurrencyOptions { get => _currencyOptions; private set { _currencyOptions = value; OnPropertyChanged(nameof(CurrencyOptions)); } }
        public ObservableCollection<string> GuaranteeTypeOptions { get => _guaranteeTypeOptions; private set { _guaranteeTypeOptions = value; OnPropertyChanged(nameof(GuaranteeTypeOptions)); } }
        public ObservableCollection<string> GuaranteeStatusOptions { get => _guaranteeStatusOptions; private set { _guaranteeStatusOptions = value; OnPropertyChanged(nameof(GuaranteeStatusOptions)); } }

        // TransactionType options are now owned by the ViewModel (VM.TransactionTypeOptions)

        // Service locator removal: _optionsService is hydrated ONCE in the DI ctor
        // (ReconciliationView(..)) via App.ServiceProvider. Loaders below call this
        // helper, which only fabricates a local fallback if DI didn't provide one —
        // we never reach into App.ServiceProvider here.
        private void EnsureOptionsService()
        {
            if (_optionsService != null) return;
            if (_reconciliationService == null) return;
            _optionsService = new OptionsService(
                _reconciliationService,
                new ReferentialService(_offlineFirstService, _reconciliationService?.CurrentUser),
                new LookupService(_offlineFirstService));
        }

        // Resolve IUserFieldsRepository ONCE from App.ServiceProvider and cache it.
        // If DI is unavailable or the registration is missing, the field stays null
        // and the legacy AllUserFields path (i.e. _offlineFirstService.UserFields) is used.
        private void EnsureUserFieldsRepository()
        {
            if (_userFieldsRepoResolved) return;
            _userFieldsRepoResolved = true;
            try
            {
                var sp = App.ServiceProvider;
                if (sp != null)
                {
                    _userFieldsRepo = sp.GetService<IUserFieldsRepository>();
                }
            }
            catch { _userFieldsRepo = null; }
        }

        // Fetches the user-field list, preferring IUserFieldsRepository when available.
        //
        // PopulateReferentialOptions() is synchronous and is called from an async
        // initializer in DataLoading.cs (InitializeFromServices). The migration spec
        // forbids us from changing the caller, so we cannot await here. The repo call
        // is bridged synchronously: in the current Infrastructure implementation the
        // repo is backed by the same in-memory cache as _offlineFirstService.UserFields
        // (no network/OleDb hit during normal init), so blocking is acceptable.
        //
        // On ANY failure we fall back to the legacy AllUserFields path so behavior is
        // preserved bit-for-bit if DI/repo is misconfigured.
        // TODO (post-migration): expose an async PopulateReferentialOptionsAsync and
        //   update DataLoading.cs to await it; then drop this sync bridge.
        private IReadOnlyList<UserField> GetUserFieldsForOptions()
        {
            EnsureUserFieldsRepository();
            if (_userFieldsRepo != null)
            {
                try
                {
                    return _userFieldsRepo.GetAllAsync(CancellationToken.None)
                                          .ConfigureAwait(false)
                                          .GetAwaiter()
                                          .GetResult();
                }
                catch
                {
                    // Fall through to the legacy cached path.
                }
            }
            return AllUserFields;
        }

        // Build Action/KPI/Incident user-field referential options
        private void PopulateReferentialOptions()
        {
            try
            {
                ActionOptions.Clear();
                KpiOptions.Clear();
                IncidentTypeOptions.Clear();

                // Vague 6 migration: prefer IUserFieldsRepository (T_Ref_User_Fields)
                // with graceful fallback to _offlineFirstService.UserFields via AllUserFields.
                var all = GetUserFieldsForOptions() ?? Array.Empty<UserField>();

                foreach (var uf in all.Where(u => string.Equals(u.USR_Category, "Action", StringComparison.OrdinalIgnoreCase))
                                       .OrderBy(u => u.USR_FieldName))
                {
                    ActionOptions.Add(new OptionItem { Id = uf.USR_ID, Name = uf.USR_FieldName });
                }
                foreach (var uf in all.Where(u => string.Equals(u.USR_Category, "KPI", StringComparison.OrdinalIgnoreCase))
                                       .OrderBy(u => u.USR_FieldName))
                {
                    KpiOptions.Add(new OptionItem { Id = uf.USR_ID, Name = uf.USR_FieldName });
                }
                foreach (var uf in all.Where(u =>
                                                string.Equals(u.USR_Category, "Incident Type", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(u.USR_Category, "INC", StringComparison.OrdinalIgnoreCase))
                                       .OrderBy(u => u.USR_FieldName))
                {
                    IncidentTypeOptions.Add(new OptionItem { Id = uf.USR_ID, Name = uf.USR_FieldName });
                }
            }
            catch { }
        }

        // Load Assignee options (users)
        private async Task LoadAssigneeOptionsAsync()
        {
            try
            {
                if (_reconciliationService == null) return;
                EnsureOptionsService();
                if (_optionsService == null) return;
                var users = await _optionsService.GetUsersAsync();
                AssigneeOptions.Clear();
                AssigneeOptions.Add(new UserOption { Id = null, Name = string.Empty });
                foreach (var u in users)
                {
                    AssigneeOptions.Add(new UserOption { Id = u.Id, Name = string.IsNullOrWhiteSpace(u.Name) ? u.Id : u.Name });
                }
            }
            catch { }
        }

        // Load currency options
        private async Task LoadCurrencyOptionsAsync()
        {
            try
            {
                if (_reconciliationService == null) return;
                EnsureOptionsService();
                if (_optionsService == null) return;
                var countryId = _currentCountryId ?? _offlineFirstService?.CurrentCountry?.CNT_Id;
                CurrencyOptions.Clear();
                CurrencyOptions.Add(string.Empty);
                if (string.IsNullOrWhiteSpace(countryId)) return;
                var list = await _optionsService.GetCurrenciesAsync(countryId);
                foreach (var s in list)
                {
                    if (!string.IsNullOrWhiteSpace(s)) CurrencyOptions.Add(s);
                }
            }
            catch { }
        }

        // Load Guarantee Status values
        private async Task LoadGuaranteeStatusOptionsAsync()
        {
            try
            {
                if (_reconciliationService == null) return;
                EnsureOptionsService();
                if (_optionsService == null) return;
                GuaranteeStatusOptions.Clear();
                GuaranteeStatusOptions.Add(string.Empty);
                var list = await _optionsService.GetGuaranteeStatusesAsync();
                foreach (var s in list)
                {
                    if (!string.IsNullOrWhiteSpace(s)) GuaranteeStatusOptions.Add(s);
                }
            }
            catch { }
        }

        // Load Guarantee Type values (mapped to UI-friendly display)
        private async Task LoadGuaranteeTypeOptionsAsync()
        {
            try
            {
                if (_reconciliationService == null) return;
                EnsureOptionsService();
                if (_optionsService == null) return;
                GuaranteeTypeOptions.Clear();
                GuaranteeTypeOptions.Add(string.Empty);
                var raw = await _optionsService.GetGuaranteeTypesAsync();
                var conv = new GuaranteeTypeDisplayConverter();
                foreach (var code in raw)
                {
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    var ui = conv.Convert(code, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)?.ToString();
                    if (!string.IsNullOrWhiteSpace(ui) && !GuaranteeTypeOptions.Any(s => string.Equals(s, ui, StringComparison.OrdinalIgnoreCase)))
                        GuaranteeTypeOptions.Add(ui);
                }
            }
            catch { }
        }
    }
}
