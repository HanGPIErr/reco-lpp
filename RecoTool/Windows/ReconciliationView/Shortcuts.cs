using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RecoTool.Services.DTOs;

namespace RecoTool.Windows
{
    // Partial: Keyboard shortcuts and toolbar button handlers for ReconciliationView
    public partial class ReconciliationView
    {
        #region Shortcut Commands

        public static readonly RoutedCommand ShortcutLinkingBasketCommand = new RoutedCommand("ShortcutLinkingBasket", typeof(ReconciliationView));
        public static readonly RoutedCommand ShortcutOpenGroupedCommand = new RoutedCommand("ShortcutOpenGrouped", typeof(ReconciliationView));
        public static readonly RoutedCommand ShortcutMarkDoneCommand = new RoutedCommand("ShortcutMarkDone", typeof(ReconciliationView));
        public static readonly RoutedCommand ShortcutProcessDwingsCommand = new RoutedCommand("ShortcutProcessDwings", typeof(ReconciliationView));
        public static readonly RoutedCommand ShortcutSetCommentCommand = new RoutedCommand("ShortcutSetComment", typeof(ReconciliationView));
        public static readonly RoutedCommand ShortcutSetReminderCommand = new RoutedCommand("ShortcutSetReminder", typeof(ReconciliationView));

        private void InitializeShortcutCommands()
        {
            CommandBindings.Add(new CommandBinding(ShortcutLinkingBasketCommand, (s, e) => ShortcutLinkingBasket_Click(s, null)));
            CommandBindings.Add(new CommandBinding(ShortcutOpenGroupedCommand, (s, e) => ShortcutOpenGrouped_Click(s, null)));
            CommandBindings.Add(new CommandBinding(ShortcutMarkDoneCommand, (s, e) => ShortcutMarkDone_Click(s, null)));
            CommandBindings.Add(new CommandBinding(ShortcutProcessDwingsCommand, (s, e) => ShortcutProcessDwings_Click(s, null)));
            CommandBindings.Add(new CommandBinding(ShortcutSetCommentCommand, (s, e) => ShortcutComment_Click(s, null)));
            CommandBindings.Add(new CommandBinding(ShortcutSetReminderCommand, (s, e) => ShortcutReminder_Click(s, null)));
        }

        #endregion

        #region Shortcut Click Handlers

        private ReconciliationViewData GetFirstSelectedRow() 
        {
            try
            {
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return null;
                return sfGrid.SelectedItems?.OfType<ReconciliationViewData>().FirstOrDefault();
            }
            catch { return null; }
        }

        internal void ShortcutLinkingBasket_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return;
                var rows = sfGrid.SelectedItems?.OfType<ReconciliationViewData>().ToList();
                if (rows == null || rows.Count == 0) { ShowToast("Select row(s) first"); return; }
                AddToLinkingBasketRequested?.Invoke(rows);
                ShowToast($"\ud83d\udd17 {rows.Count} row(s) added to linking basket");
            }
            catch (Exception ex) { ShowError($"Linking basket: {ex.Message}"); }
        }

        internal void ShortcutOpenGrouped_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = GetFirstSelectedRow();
                if (row == null) { ShowToast("Select a row first"); return; }
                if (!row.IsMatchedAcrossAccounts) { ShowToast("This row has no cross-account match"); return; }
                OpenMatchedPopup(row);
            }
            catch (Exception ex) { ShowError($"Open grouped: {ex.Message}"); }
        }

        internal void ShortcutMarkDone_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = GetFirstSelectedRow();
                if (row == null) { ShowToast("Select row(s) first"); return; }
                // Delegate to the existing handler via a synthetic FrameworkElement with DataContext
                var fe = new FrameworkElement { DataContext = row };
                QuickMarkActionDoneMenuItem_Click(fe, null);
            }
            catch (Exception ex) { ShowError($"Mark done: {ex.Message}"); }
        }

        internal void ShortcutComment_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null || sfGrid.SelectedItems?.Count == 0) { ShowToast("Select row(s) first"); return; }
                QuickSetCommentMenuItem_Click(null, null);
            }
            catch (Exception ex) { ShowError($"Comment: {ex.Message}"); }
        }

        internal void ShortcutReminder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = GetFirstSelectedRow();
                if (row == null) { ShowToast("Select row(s) first"); return; }
                var fe = new FrameworkElement { DataContext = row };
                QuickSetReminderMenuItem_Click(fe, null);
            }
            catch (Exception ex) { ShowError($"Reminder: {ex.Message}"); }
        }

        internal void ShortcutProcessDwings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = GetFirstSelectedRow();
                if (row == null) { ShowToast("Select row(s) first"); return; }
                var fe = new FrameworkElement { DataContext = row };
                SingleProcessDwings_Click(fe, null);
            }
            catch (Exception ex) { ShowError($"DWINGS: {ex.Message}"); }
        }

        #endregion
    }
}
