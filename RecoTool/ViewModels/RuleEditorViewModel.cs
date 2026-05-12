using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using RecoTool.Domain.Repositories;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.Rules;
using RecoTool.Services.UI;
using RecoTool.UI.Models;

namespace RecoTool.ViewModels
{
    /// <summary>
    /// ViewModel pour <c>RuleEditorWindow.xaml</c>. Édite une instance de
    /// <see cref="TruthRule"/> dans un dialog modal. La règle entrante est
    /// clonée pour permettre l'annulation sans effets de bord.
    ///
    /// <para>
    /// Sortie : <see cref="ResultRule"/> (null si annulation) + <see cref="RunNow"/>
    /// (flag indiquant si les règles doivent s'exécuter immédiatement après save).
    /// </para>
    ///
    /// <para>
    /// **Bind-compat** : Les collections <see cref="BoolChoices"/>,
    /// <see cref="MtStatusChoices"/> et <see cref="ActionStatusChoices"/> exposent
    /// des objets avec <c>Label</c>/<c>Value</c> pour matcher le XAML existant
    /// (qui utilise <c>SelectedValuePath="Value" DisplayMemberPath="Label"</c>).
    /// </para>
    /// </summary>
    public sealed class RuleEditorViewModel : ViewModelBase
    {
        private readonly IOfflineFirstService _offline;
        private readonly IDialogService _dialog;
        // Optional T_Ref_User_Fields repository (Vague 7 consumer migration).
        // When non-null, the VM prefers reading from the repository over the legacy
        // _offline.UserFields cached property. Stays null on the existing 3-arg ctor
        // overload used by RulesAdminWindow code-behind and the test suite.
        private readonly IUserFieldsRepository _userFieldsRepo;

        private TruthRule _editedRule;
        private TruthRule _resultRule;
        private bool _runNow;
        private string _validationError;

        public RuleEditorViewModel(TruthRule seed, IOfflineFirstService offline, IDialogService dialog,
            IUserFieldsRepository userFieldsRepo = null)
        {
            _offline = offline ?? throw new ArgumentNullException(nameof(offline));
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _userFieldsRepo = userFieldsRepo; // optional — may be null in legacy/test paths

            // Clone defensively so the caller's instance is unchanged on cancel.
            _editedRule = seed?.Clone() ?? new TruthRule { Enabled = true };

            ActionOptions = new ObservableCollection<OptionItem>();
            KpiOptions = new ObservableCollection<OptionItem>();
            IncidentTypeOptions = new ObservableCollection<OptionItem>();
            ReasonOptions = new ObservableCollection<OptionItem>();
            PopulateOptionsFromOffline();

            SaveCommand = new RelayCommand(Save, CanSave);
            CancelCommand = new RelayCommand(Cancel);
        }

        // ── Labelled choice classes for XAML binding (DisplayMemberPath=Label, SelectedValuePath=Value) ──

        /// <summary>Tri-state Yes/No/None for nullable boolean rule predicates.</summary>
        public sealed class BoolChoice
        {
            public string Label { get; set; }
            public bool? Value { get; set; }
        }

        /// <summary>Pair Label/typed-value for enum-style choices.</summary>
        public sealed class EnumChoice<T> where T : struct
        {
            public string Label { get; set; }
            public T Value { get; set; }
        }

        // ── Static / dropdown choices ──

        public RuleScope[] Scopes { get; } = (RuleScope[])Enum.GetValues(typeof(RuleScope));
        public RuleMode[] RuleModes { get; } = (RuleMode[])Enum.GetValues(typeof(RuleMode));
        public ApplyTarget[] ApplyTargets { get; } = (ApplyTarget[])Enum.GetValues(typeof(ApplyTarget));
        public string[] AccountSides { get; } = new[] { "*", "P", "R" };
        public string[] Signs { get; } = new[] { "*", "C", "D" };
        public string[] GuaranteeTypes { get; } = new[] { "*", "ISSUANCE", "REISSUANCE", "ADVISING" };
        public string[] TransactionTypes { get; } = BuildTransactionTypes();

        /// <summary>Bool choices with Label/Value for XAML binding (None / Yes / No).</summary>
        public ObservableCollection<BoolChoice> BoolChoices { get; } = new ObservableCollection<BoolChoice>
        {
            new BoolChoice { Label = "— (None) —", Value = null },
            new BoolChoice { Label = "Yes", Value = true },
            new BoolChoice { Label = "No", Value = false },
        };

        /// <summary>Tri-state Action Status (None / PENDING=false / DONE=true).</summary>
        public ObservableCollection<BoolChoice> ActionStatusChoices { get; } = new ObservableCollection<BoolChoice>
        {
            new BoolChoice { Label = "— (None) —", Value = null },
            new BoolChoice { Label = "PENDING", Value = false },
            new BoolChoice { Label = "DONE", Value = true },
        };

        /// <summary>MtStatus condition choices for XAML binding (with Label).</summary>
        public ObservableCollection<EnumChoice<MtStatusCondition>> MtStatusChoices { get; } = new ObservableCollection<EnumChoice<MtStatusCondition>>
        {
            new EnumChoice<MtStatusCondition> { Label = "* (Don't check)", Value = MtStatusCondition.Wildcard },
            new EnumChoice<MtStatusCondition> { Label = "ACKED", Value = MtStatusCondition.Acked },
            new EnumChoice<MtStatusCondition> { Label = "NOT ACKED", Value = MtStatusCondition.NotAcked },
            new EnumChoice<MtStatusCondition> { Label = "NULL (Empty)", Value = MtStatusCondition.Null },
        };

        // ── Per-tab referential options ──

        public ObservableCollection<OptionItem> ActionOptions { get; }
        public ObservableCollection<OptionItem> KpiOptions { get; }
        public ObservableCollection<OptionItem> IncidentTypeOptions { get; }
        public ObservableCollection<OptionItem> ReasonOptions { get; }

        // ── Editable rule ──

        public TruthRule EditedRule
        {
            get => _editedRule;
            set => SetField(ref _editedRule, value);
        }

        /// <summary>Result after Save, null on Cancel.</summary>
        public TruthRule ResultRule
        {
            get => _resultRule;
            private set => SetField(ref _resultRule, value);
        }

        /// <summary>Whether the caller should immediately run the rules engine after save.</summary>
        public bool RunNow
        {
            get => _runNow;
            set => SetField(ref _runNow, value);
        }

        public string ValidationError
        {
            get => _validationError;
            set => SetField(ref _validationError, value);
        }

        public bool IsSaved => _resultRule != null;

        // ── Commands ──

        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        // ── Events ──

        public event EventHandler<bool> CloseRequested; // arg = saved?

        // ── Operations ──

        private bool CanSave()
            => !string.IsNullOrWhiteSpace(_editedRule?.RuleId);

        private void Save()
        {
            ValidationError = null;
            if (!CanSave())
            {
                ValidationError = "RuleId is required.";
                return;
            }
            ResultRule = _editedRule.Clone();
            OnPropertyChanged(nameof(IsSaved));
            CloseRequested?.Invoke(this, true);
        }

        private void Cancel()
        {
            ResultRule = null;
            CloseRequested?.Invoke(this, false);
        }

        private void PopulateOptionsFromOffline()
        {
            // Vague 7 migration: prefer IUserFieldsRepository (T_Ref_User_Fields) when
            // injected. Falls back to the legacy _offline.UserFields cached property
            // on any failure (or when no repo was supplied) so behaviour is preserved
            // bit-for-bit on the existing 3-arg ctor used by tests + RulesAdminWindow.
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

            Fill(ActionOptions, fields.Where(f => string.Equals(f.USR_Category, "Action", StringComparison.OrdinalIgnoreCase)));
            Fill(KpiOptions, fields.Where(f => string.Equals(f.USR_Category, "KPI", StringComparison.OrdinalIgnoreCase)));
            Fill(IncidentTypeOptions, fields.Where(f =>
                string.Equals(f.USR_Category, "INC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.USR_Category, "Incident Type", StringComparison.OrdinalIgnoreCase)));
            Fill(ReasonOptions, fields.Where(f =>
                string.Equals(f.USR_Category, "Reason", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.USR_Category, "ReasonNonRisky", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.USR_Category, "RISKY", StringComparison.OrdinalIgnoreCase)));
        }

        private static void Fill(ObservableCollection<OptionItem> col, IEnumerable<UserField> source)
        {
            col.Clear();
            foreach (var f in source.OrderBy(f => f.USR_FieldName))
                col.Add(new OptionItem { Id = f.USR_ID, Name = f.USR_FieldName ?? f.USR_FieldDescription ?? $"#{f.USR_ID}" });
        }

        private static string[] BuildTransactionTypes()
        {
            var list = new List<string> { "*" };
            try { list.AddRange(Enum.GetNames(typeof(TransactionType))); } catch { }
            return list.ToArray();
        }
    }
}
