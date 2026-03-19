using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RecoTool.API;

namespace RecoTool.Windows
{
    public partial class SpiritGeneSearchWindow : Window
    {
        private readonly SpiritGene _spiritGene;

        public SpiritGeneSearchWindow(SpiritGene spiritGene, DateTime? operationDate = null, decimal? amount = null, string bic = null)
        {
            InitializeComponent();
            _spiritGene = spiritGene;

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
                DirectionCombo.SelectedIndex = amount.Value >= 0 ? 0 : 1; // R for positive, E for negative
            }

            if (!string.IsNullOrWhiteSpace(bic))
                BicBox.Text = bic;
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
                var bic = BicBox.Text?.Trim() ?? "";

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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
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
