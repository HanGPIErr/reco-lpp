using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;          
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using RecoTool.API;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.DTOs;

namespace RecoTool.Windows
{
    public partial class DwingsButtonsWindow : Window
    {
        private readonly OfflineFirstService _offlineFirstService;
        private readonly ReconciliationService _reconciliationService;
        private readonly Country _country;

        // -----------------------------------------------------------------
        // 1️⃣  Collection observable (liaison directe au DataGrid)
        // -----------------------------------------------------------------
        private ObservableCollection<DwingsTriggerItem> _items = new ObservableCollection<DwingsTriggerItem>();

        public DwingsButtonsWindow(
            OfflineFirstService offlineFirstService,
            ReconciliationService reconciliationService,
            Country Country)
        {
            InitializeComponent();
            _offlineFirstService = offlineFirstService;
            _reconciliationService = reconciliationService;
            _country = Country;

            Loaded += async (_, __) => await LoadDataAsync();
        }

        // -----------------------------------------------------------------
        // 2️⃣  Chargement des lignes -> remplissage de l’ObservableCollection
        // -----------------------------------------------------------------
        //private async Task LoadDataAsync()
        //{
        //    try
        //    {
        //        GridReconciliations.ItemsSource = null;

        //        // ----> 1️⃣ récupération des données brut
        //        var viewData = await _reconciliationService
        //            .GetReconciliationViewAsync(_country.CNT_Id, null, false) ?? new List<ReconciliationViewData>();

        //        var country = _offlineFirstService?.CurrentCountry;
        //        var receivableId = country?.CNT_AmbreReceivable;

        //        // ----> 2️⃣ filtre & agrégat
        //        var filtered = viewData
        //            .Where(r => r.Action == (int)ActionType.Trigger
        //                     && r.ActionStatus == false
        //                     && !string.IsNullOrEmpty(r.DWINGS_BGPMT)
        //                     && !r.IsDeleted)
        //            .ToList();

        //        var grouped = filtered
        //                .GroupBy(r => r.DWINGS_BGPMT)
        //                // ==== AJOUT DE LA CONDITION ==== 
        //                // on garde uniquement les groupes qui ont AU MINIMUM
        //                //   - une ligne receivable   (Account_ID == receivableId) ET pivot(Account_ID != receivableId)
        //                .Where(g => g.Any(x => x.Account_ID == receivableId)          // au moins 1 receivable
        //                         && g.Any(x => x.Account_ID != receivableId))       // au moins 1 pivot
        //                .Select(g => new
        //                {
        //                    BGPMT = g.Key,
        //                    Items = g.ToList(),
        //                    First = g.First(),
        //                    TotalAmount = g.Where(a => a.Account_ID == receivableId).Sum(x => x.SignedAmount),
        //                    IsGrouped = g.Any(x => x.IsMatchedAcrossAccounts),
        //                    AllIDs = string.Join(",", g.Select(x => x.ID))
        //                })
        //    .ToList();


        //        // ----> 3️⃣ création des DTO affichables
        //        var newItems = grouped
        //            .Select(g => new DwingsTriggerItem
        //            {
        //                ID = g.AllIDs,
        //                DWINGS_GuaranteeID = g.First.DWINGS_GuaranteeID,
        //                DWINGS_InvoiceID = g.First.DWINGS_InvoiceID,
        //                DWINGS_BGPMT = g.BGPMT,
        //                Amount = g.TotalAmount,
        //                RequestedAmount = g.First.I_REQUESTED_INVOICE_AMOUNT,
        //                Currency = g.First.I_BILLING_CURRENCY,
        //                Comments = g.First.Comments,

        //                // ----------------------------------------------------
        //                // Recherche sécurisée du PaymentReference
        //                // ----------------------------------------------------
        //                PaymentReference = GetFirstNonEmpty(
        //                                    g.Items.Where(i => i.Account_ID != receivableId)
        //                                          .Select(i => (i.Reconciliation_Num?.Length > 3) ? i.Reconciliation_Num : null),

        //                                    g.Items.Where(i => i.Account_ID != receivableId)
        //                                          .Select(i => i.PaymentReference),

        //                                    g.Items.Where(i => i.Account_ID != receivableId)
        //                                          .Select(i => i.Pivot_TRNFromLabel)
        //                                ),

        //                                IsGrouped = g.IsGrouped,

        //                // ----------------------------------------------------
        //                // ValueDate : on prend le premier élément qui correspond,
        //                // mais on protège contre les collections vides.
        //                // ----------------------------------------------------
        //                ValueDate = g.Items
        //                                             .Where(i => i.Account_ID != receivableId)
        //                                             .Select(i => i.Value_Date)
        //                                             .FirstOrDefault(),   // retourne default(DateTime) (= 01/01/0001) si aucun élément

        //                LineCount = g.Items.Count,

        //                // Les propriétés « Allowed » et « Result » sont initialisées à leurs
        //                // valeurs par défaut par le constructeur de DwingsTriggerItem.
        //            }).ToList();

        //        // ----- 4️⃣ (re)remplir l’ObservableCollection
        //        _items.Clear();
        //        foreach (var it in newItems) _items.Add(it);

        //        GridReconciliations.ItemsSource = _items;   // bind once – les changements seront suivis
        //        Progress.Minimum = 0;
        //        Progress.Maximum = _items.Count == 0 ? 1 : _items.Count;
        //        Progress.Value = 0;
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show(this,
        //            $"Error during loading: {ex.Message}",
        //            "Error",
        //            MessageBoxButton.OK,
        //            MessageBoxImage.Error);
        //    }
        //}
        private async Task LoadDataAsync()
        {
            try
            {
                GridReconciliations.ItemsSource = null;

                // ------------------------------------------------------------
                // 1️⃣ Récupération des données brutes
                // ------------------------------------------------------------
                var viewData = await _reconciliationService
                    .GetReconciliationViewAsync(_country.CNT_Id, null, false)
                    ?? new List<ReconciliationViewData>();

                var country = _offlineFirstService?.CurrentCountry;
                var receivableId = country?.CNT_AmbreReceivable;

                // ------------------------------------------------------------
                // 2️⃣ Filtrage des lignes receivable (on garde toutes)
                // ------------------------------------------------------------
                var receivableLines = viewData
                    .Where(r => string.Equals(r.Account_ID, receivableId, StringComparison.OrdinalIgnoreCase)
                             && r.Action == (int)ActionType.Trigger
                             && r.ActionStatus == false
                             && !r.IsDeleted
                             && (!string.IsNullOrWhiteSpace(r.InternalInvoiceReference) ||
                                  !string.IsNullOrWhiteSpace(r.DWINGS_BGPMT)))
                    .ToList();

                // ------------------------------------------------------------
                // 3️⃣ Construire **une** ligne UI pour chaque receivable qui possède ≥1 pivot
                // ------------------------------------------------------------
                var newItems = receivableLines
                    .Select(r =>
                    {
                // ---- quelle clé (type+valeur) doit être utilisée ? ----
                bool hasInvoiceRef = !string.IsNullOrWhiteSpace(r.InternalInvoiceReference);
                        string keyType = hasInvoiceRef ? "INV" : "BGPMT";
                        string keyValue = hasInvoiceRef ? r.InternalInvoiceReference.Trim()
                                                        : r.DWINGS_BGPMT.Trim();

                // ---- recherche des pivots correspondants ----
                // Pivots only need to exist and share the same key — they don't need Action==Trigger
                var pivotItems = viewData
                            .Where(p => !string.Equals(p.Account_ID, receivableId, StringComparison.OrdinalIgnoreCase)
                                     && !p.IsDeleted
                                     && (keyType == "INV"
                                         ? string.Equals(p.InternalInvoiceReference?.Trim(), keyValue, StringComparison.OrdinalIgnoreCase)
                                         : string.Equals(p.DWINGS_BGPMT?.Trim(), keyValue, StringComparison.OrdinalIgnoreCase)))
                            .ToList();               // 0, 1 ou plusieurs pivots

                // ---- on ne garde la receivable que s’il y a au moins un pivot ----
                if (!pivotItems.Any())
                            return null;               // le groupe sera éliminé plus bas

                // ---- concaténation des IDs (receivable + ses pivots) ----
                var allIds = new List<string> { r.ID.ToString() };
                        allIds.AddRange(pivotItems.Select(p => p.ID.ToString()));

                // ---- création de l’objet affichable ----
                return new DwingsTriggerItem
                        {
                    ID = string.Join(",", allIds),
                    DWINGS_GuaranteeID = r.DWINGS_GuaranteeID,
                    DWINGS_InvoiceID = r.DWINGS_InvoiceID,
                    DWINGS_BGPMT = r.DWINGS_BGPMT,
                    Amount = r.SignedAmount,
                    RequestedAmount = r.I_REQUESTED_INVOICE_AMOUNT,
                    Currency = r.I_BILLING_CURRENCY,
                    Comments = r.Comments,

                    // InternalInvoiceReference grouping → use InternalInvoiceReference as PaymentRef
                    // BGPMT grouping → use pivot's Reconciliation_Num > PaymentReference > Pivot_TRNFromLabel
                    PaymentReference = hasInvoiceRef
                        ? r.InternalInvoiceReference.Trim()
                        : GetFirstNonEmpty(
                                pivotItems
                                    .Where(i => i.Reconciliation_Num?.Length > 3)
                                    .Select(i => i.Reconciliation_Num),
                                pivotItems.Select(i => i.PaymentReference),
                                pivotItems.Select(i => i.Pivot_TRNFromLabel)),

                    IsGrouped = pivotItems.Any(x => x.IsMatchedAcrossAccounts),
                    ValueDate = pivotItems
                                    .Select(i => i.Value_Date)
                                    .FirstOrDefault(),
                    LineCount = allIds.Count
                        };
                    })
                    .Where(item => item != null)          // enlève les receivables sans pivot
                    .ToList();

                // ------------------------------------------------------------
                // 4️⃣ (re)remplissage de l’ObservableCollection
                // ------------------------------------------------------------
                _items.Clear();
                foreach (var it in newItems) _items.Add(it);

                GridReconciliations.ItemsSource = _items; // binding unique

                // ------------------------------------------------------------
                // 5️⃣ Mise à jour de la barre de progression
                // ------------------------------------------------------------
                Progress.Minimum = 0;
                Progress.Maximum = _items.Count == 0 ? 1 : _items.Count;
                Progress.Value = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Error during loading: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Returns the first non‑null / non‑empty string found in the supplied
        /// sequences (in the order they are passed). If none is found, returns
        /// <see cref="string.Empty"/>.
        /// </summary>
        private static string GetFirstNonEmpty(params IEnumerable<string?>[] sources)
        {
            foreach (var src in sources)
            {
                // FirstOrDefault returns null if the sequence is empty
                var candidate = src.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate!;
            }

            // No valid value found → return an empty string (or any default you need)
            return string.Empty;
        }

        // -----------------------------------------------------------------
        // 3️⃣  Traitement « Bulk » – mise à jour des propriétés du DTO
        // -----------------------------------------------------------------
        private async void BulkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var list = _items?.ToList() ?? new List<DwingsTriggerItem>();
                if (!list.Any())
                {
                    MessageBox.Show(this, "No rows to process.", "Information",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // ---- validation -------------------------------------------------
                var missingRef = list.Where(r => string.IsNullOrWhiteSpace(r.PaymentReference)).ToList();
                if (missingRef.Any())
                {
                    MessageBox.Show(this,
                        $"{missingRef.Count} row(s) are missing Payment Reference. Please fill them before processing.",
                        "Validation Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var nonGroupedManual = list
                    .Where(r => !r.IsGrouped && !string.IsNullOrWhiteSpace(r.PaymentReference))
                    .ToList();

                if (nonGroupedManual.Any())
                {
                    var answer = MessageBox.Show(this,
                        $"WARNING: {nonGroupedManual.Count} line(s) are NOT grouped but have a manual Payment Reference.\n\n" +
                        "This means the trigger was set manually in ReconciliationView without proper grouping.\n" +
                        "Do you want to continue anyway?",
                        "Non-Grouped Lines Warning",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (answer != MessageBoxResult.Yes) return;
                }

                // ---- désactivation UI -------------------------------------------------
                (sender as FrameworkElement)!.IsEnabled = false;
                Progress.Value = 0;

                var updated = new List<Reconciliation>();
                var dw = new Dwings();

                dw.GetUserInfo();

                var rowsUpdated = 0;

                // ---- boucle de traitement -------------------------------------------------
                foreach (var item in list)
                {
                    (bool ok, string msg) = dw.Dwings_PressBlueButton(item.PaymentReference, item.ValueDate.Value, item.RequestedAmount, _country.CNT_DWID, item.Currency, item.DWINGS_BGPMT);
                    item.Result = msg;

                    if (ok)
                    {
                        // Tous les IDs de la ligne groupée (séparés par virgule)
                        var ids = item.ID.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var id in ids)
                        {
                            var reco = await _reconciliationService.GetReconciliationByIdAsync(_country.CNT_Id, id.Trim());
                            if (reco != null)
                            {
                                reco.ActionStatus = true;
                                reco.TriggerDate = DateTime.UtcNow;

                                // Si la ligne n’est pas groupée et que l’utilisateur a saisi un
                                // PaymentReference, on le sauvegarde.
                                if (!item.IsGrouped && !string.IsNullOrWhiteSpace(item.PaymentReference))
                                    reco.PaymentReference = item.PaymentReference;

                                updated.Add(reco);
                            }
                        }
                    }

                    rowsUpdated++;
                    Progress.Value += 1;
                }

                // ---- persistance -------------------------------------------------
                if (updated.Any())
                {
                    await _reconciliationService.SaveReconciliationsAsync(updated);
                    try { await _offlineFirstService.PushReconciliationIfPendingAsync(_country.CNT_Id); }
                    catch { /* ignore – best‑effort */ }

                    // rafraîchit la grille pour récupérer d’éventuels changements côté DB
                    await LoadDataAsync();
                    try { RefreshOpenReconciliationViews(); } catch { }
                }

                MessageBox.Show(this,
                    $"Processing completed. Success: {rowsUpdated}/{list.Count}",
                    "Completed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Error during processing: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                (sender as FrameworkElement)!.IsEnabled = true;
            }
        }

        private void RefreshOpenReconciliationViews()
        {
            try
            {
                foreach (Window w in Application.Current.Windows)
                {
                    if (w == null) continue;
                    foreach (var view in FindVisualChildren<ReconciliationView>(w))
                    {
                        try { view.Refresh(); } catch { }
                    }
                }
            }
            catch { }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(depObj);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                if (child is T t)
                    yield return t;
                foreach (var childOfChild in FindVisualChildren<T>(child))
                    yield return childOfChild;
            }
        }
    }

    // ==============================================================
    // DTO affiché dans le DataGrid – implémentation correcte de INotifyPropertyChanged
    // ==============================================================

    public class DwingsTriggerItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // Helper qui utilise CallerMemberName → plus besoin d’écrire le nom explicitement
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        // ------------------- propriétés simples (pas de logique) -------------------
        public string ID { get; set; } = string.Empty;
        public string DWINGS_GuaranteeID { get; set; } = string.Empty;
        public string DWINGS_InvoiceID { get; set; } = string.Empty;
        public string DWINGS_BGPMT { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string Comments { get; set; } = string.Empty;
        public bool IsGrouped { get; set; }
        public DateTime? ValueDate { get; set; }
        public int LineCount { get; set; }
        public string RequestedAmount { get; set; }

        // ------------------- propriétés qui changent à l’exécution --------------

        private bool _allowed;
        public bool Allowed
        {
            get => _allowed;
            set
            {
                if (_allowed != value)
                {
                    _allowed = value;
                    OnPropertyChanged();               // le nom de la prop est ajouté automatiquement
                }
            }
        }

        private string _result = string.Empty;
        public string Result
        {
            get => _result;
            set
            {
                if (_result != value)
                {
                    _result = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _paymentReference = string.Empty;
        public string PaymentReference
        {
            get => _paymentReference;
            set
            {
                if (_paymentReference != value)
                {
                    _paymentReference = value;
                    OnPropertyChanged();
                }
            }
        }
    }
}