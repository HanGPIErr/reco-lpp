using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        private SpiritGeneResultRow _selectedRow;
        private bool _detailsLoaded = false;

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
                // Use exact amount as both min and max
                AmountMinBox.Text = abs.ToString("F2");
                AmountMaxBox.Text = abs.ToString("F2");

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
                if (amountMax < amountMin) amountMax = amountMin;

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
                        CCsm = op.CCsm,
                        Sens = direction,
                        DetailStatus = "Loading..."
                    }).ToList();

                    ResultsGrid.ItemsSource = displayList;
                    CountText.Text = $"{displayList.Count} transaction(s) found";
                    StatusText.Text = $"Found {displayList.Count} transaction(s)";

                    // If less than 10 results, preload details automatically
                    if (displayList.Count < 10)
                    {
                        StatusText.Text = "Loading transaction details...";
                        await LoadDetailsForAllTransactions(displayList);
                        StatusText.Text = $"Ready - {displayList.Count} transactions with details";
                    }
                    else
                    {
                        StatusText.Text = $"Ready - {displayList.Count} transactions (click to load details)";
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

        private async Task LoadDetailsForAllTransactions(List<SpiritGeneResultRow> transactions)
        {
            var tasks = transactions.Select(async row =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(row.ITransid) || !string.IsNullOrWhiteSpace(row.IMsgId))
                    {
                        var detail = await _spiritGene.GetTransactionDetails(row.ITransid, row.IMsgId, row.Sens ?? "R");
                        if (detail != null)
                        {
                            row.TransactionDetail = detail;
                            row.DetailStatus = "✓";
                        }
                        else
                        {
                            row.DetailStatus = "✗";
                        }
                    }
                    else
                    {
                        row.DetailStatus = "N/A";
                    }
                }
                catch
                {
                    row.DetailStatus = "✗";
                }
            });

            await Task.WhenAll(tasks);
            
            // Refresh the grid to show updated status
            if (ResultsGrid.ItemsSource != null)
            {
                var refreshed = ResultsGrid.ItemsSource.Cast<SpiritGeneResultRow>().ToList();
                ResultsGrid.ItemsSource = null;
                ResultsGrid.ItemsSource = refreshed;
            }
        }

        private async void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = ResultsGrid.SelectedItem as SpiritGeneResultRow;
            if (row == null)
            {
                ShowEmptyDetails();
                return;
            }

            _selectedRow = row;
            
            // If details are already loaded, show them
            if (row.TransactionDetail != null)
            {
                ShowTransactionDetails(row);
                return;
            }

            // If no details yet, load them (for >=10 results scenario)
            if (string.IsNullOrWhiteSpace(row.ITransid) && string.IsNullOrWhiteSpace(row.IMsgId))
            {
                ShowEmptyDetails("No Transaction ID or Message ID available for this transaction.");
                return;
            }

            try
            {
                StatusText.Text = "Loading details...";
                
                var detail = await _spiritGene.GetTransactionDetails(row.ITransid, row.IMsgId, row.Sens ?? "R");

                if (detail != null)
                {
                    row.TransactionDetail = detail;
                    row.DetailStatus = "✓";
                    ShowTransactionDetails(row);
                    
                    // Refresh grid to show updated status
                    var refreshed = ResultsGrid.ItemsSource.Cast<SpiritGeneResultRow>().ToList();
                    ResultsGrid.ItemsSource = null;
                    ResultsGrid.ItemsSource = refreshed;
                    ResultsGrid.SelectedItem = row; // Re-select the row
                }
                else
                {
                    row.DetailStatus = "✗";
                    ShowEmptyDetails("No details returned for this transaction.");
                }
            }
            catch (Exception ex)
            {
                row.DetailStatus = "✗";
                ShowEmptyDetails($"Failed to load details: {ex.Message}");
            }
            finally
            {
                StatusText.Text = string.Empty;
            }
        }

        private void ShowTransactionDetails(SpiritGeneResultRow row)
        {
            var detail = row.TransactionDetail;
            if (detail == null) return;

            DetailsPanel.Children.Clear();
            
            // Transaction Header
            var header = new TextBlock 
            { 
                Text = $"Transaction {row.ITransid ?? row.IMsgId}", 
                FontWeight = FontWeights.Bold, 
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DetailsPanel.Children.Add(header);

            // Create detail groups
            AddDetailGroup("Basic Information", new Dictionary<string, string>
            {
                ["Transaction Type"] = detail.CTypeOpe ?? "N/A",
                ["Status"] = detail.CStatOpe ?? "N/A",
                ["Execution Date"] = detail.DExeOpe ?? "N/A",
                ["Settlement Date"] = detail.DRgltOpe ?? "N/A",
                ["Currency"] = detail.CDevise ?? "N/A"
            });

            AddDetailGroup("Amount Information", new Dictionary<string, string>
            {
                ["Amount"] = detail.MOpe?.ToString("F2") ?? "N/A",
                ["Commission Amount"] = detail.MCom?.ToString("F2") ?? "N/A",
                ["Commission Tax"] = detail.MComTax?.ToString("F2") ?? "N/A"
            });

            AddDetailGroup("Debitor Information", new Dictionary<string, string>
            {
                ["Name"] = detail.LNomDo ?? "N/A",
                ["Address"] = $"{detail.LAdr1Do} {detail.LAdr2Do}".Trim(),
                ["City"] = $"{detail.LCpDo} {detail.LVilleDo}".Trim(),
                ["Country"] = detail.LPaysDo ?? "N/A",
                ["IBAN"] = detail.IIbanDo ?? "N/A",
                ["BIC"] = detail.IBicDo ?? "N/A"
            });

            AddDetailGroup("Creditor Information", new Dictionary<string, string>
            {
                ["Name"] = detail.LNomBen ?? "N/A",
                ["Address"] = $"{detail.LAdr1Ben} {detail.LAdr2Ben}".Trim(),
                ["City"] = $"{detail.LCpBen} {detail.LVilleBen}".Trim(),
                ["Country"] = detail.LPaysBen ?? "N/A",
                ["IBAN"] = detail.IIbanBen ?? "N/A",
                ["BIC"] = detail.IBicBen ?? "N/A"
            });

            AddDetailGroup("Communication", new Dictionary<string, string>
            {
                ["Message ID"] = detail.IMsgId ?? "N/A",
                ["End-to-End ID"] = detail.LEndToEndId ?? "N/A",
                ["Transaction ID"] = detail.ITransid ?? "N/A",
                ["Communication"] = detail.LCom ?? "N/A",
                ["Structured Communication"] = detail.LComStr ?? "N/A"
            });

            if (!string.IsNullOrWhiteSpace(detail.LMotifRejet))
            {
                AddDetailGroup("Rejection Information", new Dictionary<string, string>
                {
                    ["Rejection Reason"] = detail.LMotifRejet
                });
            }
        }

        private void AddDetailGroup(string title, Dictionary<string, string> details)
        {
            if (details == null || details.All(kvp => string.IsNullOrWhiteSpace(kvp.Value)))
                return;

            // Group header
            var groupHeader = new Border 
            { 
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4),
                Margin = new Thickness(0, 8, 0, 4)
            };
            var headerText = new TextBlock 
            { 
                Text = title, 
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 118, 210))
            };
            groupHeader.Child = headerText;
            DetailsPanel.Children.Add(groupHeader);

            // Detail items
            foreach (var kvp in details)
            {
                if (string.IsNullOrWhiteSpace(kvp.Value)) continue;

                var detailGrid = new Grid 
                { 
                    Margin = new Thickness(8, 2)
                };
                detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock 
                { 
                    Text = kvp.Key + ":", 
                    FontWeight = FontWeights.Medium,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(label, 0);
                
                var value = new TextBlock 
                { 
                    Text = kvp.Value, 
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                Grid.SetColumn(value, 1);

                detailGrid.Children.Add(label);
                detailGrid.Children.Add(value);
                DetailsPanel.Children.Add(detailGrid);
            }
        }

        private void ShowEmptyDetails(string message = null)
        {
            DetailsPanel.Children.Clear();
            var emptyText = new TextBlock 
            { 
                Text = message ?? "Select a transaction to view details", 
                TextAlignment = TextAlignment.Center, 
                VerticalAlignment = VerticalAlignment.Center, 
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(153, 153, 153)), 
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 40, 0, 0)
            };
            DetailsPanel.Children.Add(emptyText);
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
        public string Sens { get; set; }
        public string DetailStatus { get; set; } // ✓, ✗, N/A, Loading...
        public SpiritGeneTransactionDetailOutput.GDetOpe TransactionDetail { get; set; }
    }
}
