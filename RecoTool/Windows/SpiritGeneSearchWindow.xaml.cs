using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RecoTool.API;
using RecoTool.Models;
using RecoTool.Services;

namespace RecoTool.Windows
{
    public partial class SpiritGeneSearchWindow : Window
    {
        private readonly SpiritGene _spiritGene;

        public SpiritGeneSearchWindow(SpiritGene spiritGene, OfflineFirstService offlineFirstService, DateTime? operationDate = null, decimal? amount = null, string bic = null)
        {
            InitializeComponent();
            _spiritGene = spiritGene;

            // Populate BIC ListBox with all countries
            PopulateBicListBox(offlineFirstService, bic);

            // Pre-fill from row context
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
                var abs = Math.Abs(amount.Value);
                AmountMinBox.Text = (abs - 1).ToString("F2");
                AmountMaxBox.Text = (abs + 1).ToString("F2");

                // Set direction based on sign
                DirectionCombo.SelectedIndex = amount.Value >= 0 ? 0 : 1; // R for positive, A for negative
            }

            // BIC is handled by PopulateBicListBox
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

                var dateFrom = DateFromPicker.SelectedDate ?? DateTime.Today.AddMonths(-1);
                var dateTo = DateToPicker.SelectedDate ?? DateTime.Today;
                var bic = BicComboBox.SelectedValue as string ?? "";

                decimal amountMin = 0, amountMax = 999999999;
                decimal.TryParse(AmountMinBox.Text, out amountMin);
                decimal.TryParse(AmountMaxBox.Text, out amountMax);
                if (amountMax < amountMin) amountMax = amountMin + 100;

                var direction = (DirectionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "R";

                var result = await _spiritGene.GetTransactions(dateFrom, dateTo, bic, amountMin, amountMax, direction);

                if (result?.GListOpe != null && result.GListOpe.Count > 0)
                {
                    // Create display-friendly list
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
                        CCsm = op.CCsm
                    }).ToList();

                    ResultsGrid.ItemsSource = displayList;
                    CountText.Text = $"{displayList.Count} transaction(s) found";
                    StatusText.Text = $"Found {displayList.Count} transaction(s)";
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
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
    }
}
