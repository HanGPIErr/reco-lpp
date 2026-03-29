using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RecoTool.Services.DTOs;

namespace RecoTool.Windows
{
    // Partial: Selection helpers and bulk selection button behavior for ReconciliationView
    public partial class ReconciliationView
    {
        // Bulk selection button behavior
        // Click: Select all visible rows
        // Shift+Click: Select matched rows only
        // Ctrl+Click: Select unmatched rows only
        // Alt+Click: Clear selection
        private void BulkSelection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfGrid = FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return;

                var modifiers = Keyboard.Modifiers;
                if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
                {
                    sfGrid.SelectedItems.Clear();
                    UpdateStatusInfo("Selection cleared");
                    return;
                }

                // Helper local predicate for matched
                bool IsMatched(object obj)
                {
                    var item = obj as ReconciliationViewData;
                    if (item == null) return false;
                    return !string.IsNullOrWhiteSpace(item.DWINGS_GuaranteeID)
                           || !string.IsNullOrWhiteSpace(item.DWINGS_InvoiceID)
                           || !string.IsNullOrWhiteSpace(item.DWINGS_BGPMT)
                           || !string.IsNullOrWhiteSpace(item.GUARANTEE_ID)
                           || !string.IsNullOrWhiteSpace(item.INVOICE_ID)
                           || !string.IsNullOrWhiteSpace(item.COMMISSION_ID);
                }

                sfGrid.SelectedItems.Clear();

                // Iterate visible items via the ItemsSource
                int selected = 0;
                var itemsSource = sfGrid.ItemsSource as System.Collections.IEnumerable;
                if (itemsSource == null) return;
                foreach (var obj in itemsSource)
                {
                    var data = obj as ReconciliationViewData;
                    if (data == null) continue;

                    bool pick;
                    if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        pick = IsMatched(data);
                    else if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                        pick = !IsMatched(data);
                    else
                        pick = true; // simple click => all visible

                    if (pick)
                    {
                        sfGrid.SelectedItems.Add(data);
                        selected++;
                    }
                }

                if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    UpdateStatusInfo($"{selected} matched selected");
                else if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    UpdateStatusInfo($"{selected} unmatched selected");
                else
                    UpdateStatusInfo($"{selected} rows selected");
            }
            catch { }
        }
    }
}
