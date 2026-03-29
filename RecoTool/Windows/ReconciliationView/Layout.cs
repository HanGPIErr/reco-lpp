using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using RecoTool.UI.Helpers;

namespace RecoTool.Windows
{
    // Partial: Column layout capture/apply and header context menu for ReconciliationView
    public partial class ReconciliationView
    {
        private class ColumnSetting
        {
            public string Header { get; set; }
            public string SortMemberPath { get; set; }
            public int DisplayIndex { get; set; }
            public double? WidthValue { get; set; } // store as pixel width when possible
            public string WidthType { get; set; } // Auto, SizeToCells, SizeToHeader, Pixel
            public bool Visible { get; set; }
        }

        private class GridLayout
        {
            public List<ColumnSetting> Columns { get; set; } = new List<ColumnSetting>();
            public List<SortDescriptor> Sorts { get; set; } = new List<SortDescriptor>();
        }

        private class SortDescriptor
        {
            public string Member { get; set; }
            public ListSortDirection Direction { get; set; }
        }

        private GridLayout CaptureGridLayout()
        {
            var layout = new GridLayout();
            try
            {
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return layout;

                for (int i = 0; i < sfGrid.Columns.Count; i++)
                {
                    var col = sfGrid.Columns[i];
                    var st = new ColumnSetting
                    {
                        Header = col.HeaderText,
                        SortMemberPath = col.MappingName,
                        DisplayIndex = i,
                        Visible = !col.IsHidden,
                        WidthType = "Pixel",
                        WidthValue = col.ActualWidth > 0 ? col.ActualWidth : col.Width
                    };
                    layout.Columns.Add(st);
                }

                // Capture sort descriptions from SfDataGrid
                if (sfGrid.SortColumnDescriptions != null)
                {
                    foreach (var sd in sfGrid.SortColumnDescriptions)
                    {
                        layout.Sorts.Add(new SortDescriptor
                        {
                            Member = sd.ColumnName,
                            Direction = sd.SortDirection == ListSortDirection.Ascending
                                ? ListSortDirection.Ascending : ListSortDirection.Descending
                        });
                    }
                }
            }
            catch { }
            return layout;
        }

        private void ApplyGridLayout(GridLayout layout)
        {
            try
            {
                if (layout == null) return;
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return;

                // Helper: find column by header text first, then fall back to MappingName for compat with old saved layouts
                Syncfusion.UI.Xaml.Grid.GridColumn FindCol(ColumnSetting s)
                {
                    var c = sfGrid.Columns.FirstOrDefault(x => string.Equals(x.HeaderText, s.Header, StringComparison.OrdinalIgnoreCase));
                    if (c == null && !string.IsNullOrWhiteSpace(s.SortMemberPath))
                        c = sfGrid.Columns.FirstOrDefault(x => string.Equals(x.MappingName, s.SortMemberPath, StringComparison.OrdinalIgnoreCase));
                    return c;
                }

                // 1) Apply visibility and width
                foreach (var setting in layout.Columns)
                {
                    var col = FindCol(setting);
                    if (col == null) continue;
                    try { col.IsHidden = !setting.Visible; } catch { }
                    try
                    {
                        if (setting.WidthValue.HasValue && setting.WidthValue.Value > 0)
                            col.Width = setting.WidthValue.Value;
                    }
                    catch { }
                }

                // 2) Reorder columns to match saved DisplayIndex
                var orderedSettings = layout.Columns.OrderBy(s => s.DisplayIndex).ToList();
                for (int targetIdx = 0; targetIdx < orderedSettings.Count; targetIdx++)
                {
                    try
                    {
                        var setting = orderedSettings[targetIdx];
                        var col = FindCol(setting);
                        if (col == null) continue;
                        int currentIdx = sfGrid.Columns.IndexOf(col);
                        if (currentIdx >= 0 && currentIdx != targetIdx && targetIdx < sfGrid.Columns.Count)
                        {
                            sfGrid.Columns.RemoveAt(currentIdx);
                            sfGrid.Columns.Insert(targetIdx, col);
                        }
                    }
                    catch { }
                }

                // 3) Apply sorting
                if (sfGrid.SortColumnDescriptions != null)
                {
                    sfGrid.SortColumnDescriptions.Clear();
                    foreach (var s in layout.Sorts)
                    {
                        if (!string.IsNullOrWhiteSpace(s.Member))
                        {
                            sfGrid.SortColumnDescriptions.Add(new Syncfusion.UI.Xaml.Grid.SortColumnDescription
                            {
                                ColumnName = s.Member,
                                SortDirection = s.Direction
                            });
                        }
                    }
                    // Ensure Operation_Date ascending is always present as default sort
                    if (!sfGrid.SortColumnDescriptions.Any(sd => string.Equals(sd.ColumnName, "Operation_Date", StringComparison.OrdinalIgnoreCase)))
                    {
                        sfGrid.SortColumnDescriptions.Add(new Syncfusion.UI.Xaml.Grid.SortColumnDescription
                        {
                            ColumnName = "Operation_Date",
                            SortDirection = System.ComponentModel.ListSortDirection.Ascending
                        });
                    }
                }
            }
            catch { }
        }

        //private void ResultsDataGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        //{
        //    try
        //    {
        //        var dg = sender as DataGrid;
        //        if (dg == null) return;
        //        var dep = e.OriginalSource as DependencyObject;
        //        var header = VisualTreeHelpers.FindAncestor<DataGridColumnHeader>(dep);
        //        if (header != null)
        //        {
        //            e.Handled = true;
        //            var cm = new ContextMenu();
        //            foreach (var col in dg.Columns)
        //            {
        //                var mi = new MenuItem { Header = Convert.ToString(col.Header), IsCheckable = true, IsChecked = col.Visibility == Visibility.Visible };
        //                mi.Click += (s, ev) =>
        //                {
        //                    try
        //                    {
        //                        col.Visibility = mi.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        //                    }
        //                    catch { }
        //                };
        //                cm.Items.Add(mi);
        //            }
        //            cm.IsOpen = true;
        //        }
        //    }
        //    catch { }
        //}

        // Public helper to apply a saved grid layout from its JSON representation.
        public void ApplyLayoutJson(string layoutJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(layoutJson)) return;
                var layout = System.Text.Json.JsonSerializer.Deserialize<GridLayout>(layoutJson);
                ApplyGridLayout(layout);
            }
            catch { /* ignore invalid layout JSON */ }
        }
    }
}
