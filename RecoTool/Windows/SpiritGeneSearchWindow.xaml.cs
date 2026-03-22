using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RecoTool.API;
using RecoTool.Services;

namespace RecoTool.Windows
{
    public partial class SpiritGeneSearchWindow : Window
    {
        private readonly SpiritGene _spiritGene;
        private readonly Dictionary<SpiritGeneResultRow, SpiritGeneTransactionDetailOutput.GDetOpe> _detailCache
            = new Dictionary<SpiritGeneResultRow, SpiritGeneTransactionDetailOutput.GDetOpe>();

        public SpiritGeneSearchWindow(SpiritGene spiritGene, OfflineFirstService offlineFirstService, DateTime? operationDate = null, decimal? amount = null, string bic = null)
        {
            InitializeComponent();
            _spiritGene = spiritGene;

            PopulateBicListBox(offlineFirstService, bic);

            if (operationDate.HasValue)
            {
                DateFromPicker.SelectedDate = operationDate.Value.AddDays(-3);
                DateToPicker.SelectedDate = operationDate.Value.AddDays(3);
            }
            else
            {
                DateFromPicker.SelectedDate = DateTime.Today.AddMonths(-1);
                DateToPicker.SelectedDate = DateTime.Today;
            }

            if (amount.HasValue)
            {
                var abs = Math.Abs(amount.Value).ToString("F2", CultureInfo.InvariantCulture);
                AmountMinBox.Text = abs;
                AmountMaxBox.Text = abs;
                DirectionCombo.SelectedIndex = amount.Value >= 0 ? 0 : 1;
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_spiritGene == null)
            {
                MessageBox.Show("SpiritGene service not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                SearchButton.IsEnabled = false;
                StatusText.Text = "Searching...";
                ResultsGrid.ItemsSource = null;
                _detailCache.Clear();
                ClearDetailPanel();

                var dateFrom = DateFromPicker.SelectedDate ?? DateTime.Today.AddMonths(-1);
                var dateTo = DateToPicker.SelectedDate ?? DateTime.Today;
                var bic = BicComboBox.SelectedValue as string ?? "";
                var direction = (DirectionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "R";

                decimal amountMin = 0, amountMax = 9999999;
                decimal.TryParse(AmountMinBox.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out amountMin);
                if (!string.IsNullOrWhiteSpace(AmountMaxBox.Text))
                    decimal.TryParse(AmountMaxBox.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out amountMax);
                if (amountMax < amountMin) amountMax = amountMin;

                var result = await _spiritGene.GetTransactions(dateFrom, dateTo, bic, amountMin, amountMax, direction);

                if (result?.GListOpe != null && result.GListOpe.Count > 0)
                {
                    var displayList = result.GListOpe.Select(op => new SpiritGeneResultRow
                    {
                        CTypeOpe = op.CTypeOpe,
                        DRgltOpe = op.DRgltOpe,
                        DisplayAmount = op.MOpe?.ToString() ?? "",
                        IIbanDo = op.IIbanDo,
                        IBicDo = op.IBicDo,
                        IIbanBen = op.IIbanBen,
                        IBicBen = op.IBicBen,
                        IMsgId = op.IMsgId,
                        ITransid = op.ITransid,
                        LEndToEndId = op.LEndToEndId,
                        CCsm = op.CCsm,
                        Sens = direction
                    }).ToList();

                    ResultsGrid.ItemsSource = displayList;
                    CountText.Text = $"{displayList.Count} transaction(s)";

                    if (displayList.Count < 10)
                    {
                        StatusText.Text = $"{displayList.Count} result(s) – fetching details...";
                        await PrefetchDetailsAsync(displayList);
                        StatusText.Text = $"{displayList.Count} result(s) – details ready.";
                    }
                    else
                    {
                        StatusText.Text = $"{displayList.Count} result(s).";
                    }
                }
                else
                {
                    StatusText.Text = "No transactions found.";
                    CountText.Text = "0 transactions";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                MessageBox.Show($"Search failed: {ex.Message}", "SpiritGene Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SearchButton.IsEnabled = true;
            }
        }

        private async Task PrefetchDetailsAsync(List<SpiritGeneResultRow> rows)
        {
            foreach (var row in rows)
            {
                try
                {
                    var detail = await _spiritGene.GetTransactionDetails(row.ITransid, row.IMsgId, row.Sens ?? "R");
                    if (detail != null)
                    {
                        _detailCache[row] = detail;
                        if (ResultsGrid.SelectedItem == row)
                            ShowDetailPanel(detail, row);
                    }
                }
                catch { }
            }
        }

        private void PopulateBicListBox(OfflineFirstService offlineFirstService, string preferredBic = null)
        {
            try
            {
                var items = new List<BicListItem>();
                
                // Get all countries with BIC
                var countries = offlineFirstService.GetCountries();
                if (countries != null)
                {
                    foreach (var country in countries.Result.Where(c => !string.IsNullOrWhiteSpace(c.CNT_BIC)))
                    {
                        items.Add(new BicListItem
                        {
                            Bic = country.CNT_BIC.Trim(),
                            DisplayText = $"{country.CNT_Name} ({country.CNT_BIC.Trim()})"
                        });
                    }
                }
                
                // Sort by country name
                items = items.OrderBy(i => i.DisplayText).ToList();
                
                BicComboBox.ItemsSource = items;
                
                // Pre-select preferred BIC or current country's BIC
                string bicToSelect = preferredBic;
                if (string.IsNullOrWhiteSpace(bicToSelect))
                {
                    var currentCountry = offlineFirstService.CurrentCountry;
                    bicToSelect = currentCountry?.CNT_BIC?.Trim();
                }
                
                if (!string.IsNullOrWhiteSpace(bicToSelect))
                {
                    var item = items.FirstOrDefault(i => string.Equals(i.Bic, bicToSelect.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (item != null)
                    {
                        BicComboBox.SelectedItem = item;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating BIC ComboBox: {ex.Message}");
            }
        }

        private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = ResultsGrid.SelectedItem as SpiritGeneResultRow;
            if (row == null) { ClearDetailPanel(); return; }

            if (_detailCache.TryGetValue(row, out var cached))
            {
                ShowDetailPanel(cached, row);
                return;
            }

            DetailTitleText.Text = row.ITransid ?? row.IMsgId ?? "";
            DetailSubText.Text = "";
            DetailLoadingText.Text = "Loading...";
            DetailLoadingText.Visibility = Visibility.Visible;
            DetailFieldsControl.ItemsSource = null;
            DetailCopyButton.Visibility = Visibility.Collapsed;

            try
            {
                var detail = await _spiritGene.GetTransactionDetails(row.ITransid, row.IMsgId, row.Sens ?? "R");
                if (detail != null)
                {
                    _detailCache[row] = detail;
                    if (ResultsGrid.SelectedItem == row)
                        ShowDetailPanel(detail, row);
                }
                else if (ResultsGrid.SelectedItem == row)
                {
                    DetailLoadingText.Text = "No details available.";
                }
            }
            catch (Exception ex)
            {
                if (ResultsGrid.SelectedItem == row)
                    DetailLoadingText.Text = $"Error: {ex.Message}";
            }
        }

        private void ShowDetailPanel(SpiritGeneTransactionDetailOutput.GDetOpe detail, SpiritGeneResultRow row)
        {
            DetailTitleText.Text = row.ITransid ?? row.IMsgId ?? "";
            DetailSubText.Text = $"{detail.TypeOpe}  |  CSM: {detail.Csm}  |  {detail.EndToEndId}";
            DetailLoadingText.Visibility = Visibility.Collapsed;
            DetailFieldsControl.ItemsSource = BuildFields(detail);
            DetailCopyButton.Visibility = Visibility.Visible;
        }

        private void ClearDetailPanel()
        {
            DetailTitleText.Text = "";
            DetailSubText.Text = "";
            DetailLoadingText.Text = "Select a transaction to see details.";
            DetailLoadingText.Visibility = Visibility.Visible;
            DetailFieldsControl.ItemsSource = null;
            DetailCopyButton.Visibility = Visibility.Collapsed;
        }

        private void DetailCopyButton_Click(object sender, RoutedEventArgs e)
        {
            var items = DetailFieldsControl.ItemsSource as IEnumerable<DetailField>;
            if (items == null) return;
            var sb = new StringBuilder();
            foreach (var f in items)
                sb.AppendLine($"{f.Label}: {f.Value}");
            Clipboard.SetText(sb.ToString());
        }

        private static List<DetailField> BuildFields(SpiritGeneTransactionDetailOutput.GDetOpe detail)
        {
            if (detail == null) return new List<DetailField>();
            var fields = new List<DetailField>();
            foreach (var prop in typeof(SpiritGeneTransactionDetailOutput.GDetOpe).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var rawValue = prop.GetValue(detail);
                if (rawValue == null) continue;
                var value = rawValue.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                var label = prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? prop.Name;
                fields.Add(new DetailField { Label = label, Value = value });
            }
            return fields;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public class DetailField
    {
        public string Label { get; set; }
        public string Value { get; set; }
    }

    public class BicListItem
    {
        public string Bic { get; set; }
        public string DisplayText { get; set; }
    }

    public class SpiritGeneResultRow
    {
        public string CTypeOpe { get; set; }
        public string DRgltOpe { get; set; }
        public string DisplayAmount { get; set; }
        public string IIbanDo { get; set; }
        public string IBicDo { get; set; }
        public string IIbanBen { get; set; }
        public string IBicBen { get; set; }
        public string IMsgId { get; set; }
        public string ITransid { get; set; }
        public string LEndToEndId { get; set; }
        public string CCsm { get; set; }
        public string Sens { get; set; }
    }
}
