using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using RecoTool.Services.DTOs;

// Namespace kept at RecoTool.Windows so XAML references using
// xmlns:local="clr-namespace:RecoTool.Windows" resolve unchanged — same pattern as
// UserFieldConverters / RuleDisplayConverters.
namespace RecoTool.Windows
{
    /// <summary>
    /// Cell-level background tint for reconciliation fields that have changed since the last
    /// import snapshot. Bound from <c>GridRowStyles.xaml</c> on <c>syncfusion:GridCell</c>
    /// through a <see cref="MultiBinding"/> that feeds
    /// <c>[DataContext(ReconciliationViewData), ColumnBase.GridColumn.MappingName]</c>.
    /// </summary>
    /// <remarks>
    /// Columns with their own <c>CellStyle</c> (Action, KPI, Status…) override this global style,
    /// so they intentionally keep their semantic colouring — the row-level left border and the
    /// row tooltip still carry the "changed" signal for those cells. Returns a frozen brush in
    /// both branches so the grid's recycling path pays zero allocation per row.
    /// </remarks>
    public sealed class CellChangedBackgroundConverter : IMultiValueConverter
    {
        // Amber-200 — deliberately stronger than amber-50. The previous tint was almost invisible
        // on the default white row background, which defeated the whole point: users didn't know
        // a cell was hoverable. amber-200 stays readable against every row background we ship
        // (selected blue #DBEAFE, risky red, etc.) and still does not fight the row foreground.
        private static readonly SolidColorBrush HighlightBrush =
            CreateFrozen(Color.FromArgb(0xFF, 0xFD, 0xE6, 0x8A));

        private static readonly SolidColorBrush TransparentBrush =
            CreateFrozen(Color.FromArgb(0x00, 0x00, 0x00, 0x00));

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return TransparentBrush;
            var row = values[0] as ReconciliationViewData;
            var mappingName = values[1] as string;
            if (row == null || string.IsNullOrEmpty(mappingName)) return TransparentBrush;

            return row.GetChangeForField(mappingName) != null ? HighlightBrush : TransparentBrush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static SolidColorBrush CreateFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            if (b.CanFreeze) b.Freeze();
            return b;
        }
    }

    /// <summary>
    /// Cell-level tooltip showing the pre-import value for a field that changed since the last
    /// snapshot. Bound via the same <see cref="MultiBinding"/> feed as
    /// <see cref="CellChangedBackgroundConverter"/>.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="DependencyProperty.UnsetValue"/> for unchanged cells. <c>UnsetValue</c>
    /// tells WPF to skip the Setter entirely — crucial so the row-level tooltip (declared on the
    /// parent <c>VirtualizingCellsControl</c>) can still surface through unchanged cells instead
    /// of being shadowed by a null cell tooltip.
    /// </remarks>
    public sealed class CellChangedTooltipConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return DependencyProperty.UnsetValue;
            var row = values[0] as ReconciliationViewData;
            var mappingName = values[1] as string;
            if (row == null || string.IsNullOrEmpty(mappingName)) return DependencyProperty.UnsetValue;

            var change = row.GetChangeForField(mappingName);
            if (change == null) return DependencyProperty.UnsetValue;

            // Plain text ToolTip — WPF renders it with its default chrome, so no allocations on
            // the visual-tree side. Kept intentionally short: the row tooltip already shows the
            // full field-level diff; this one is just the "what was here before" quick glance.
            var oldV = string.IsNullOrEmpty(change.OldValue) ? "(empty)" : change.OldValue;
            var newV = string.IsNullOrEmpty(change.NewValue) ? "(empty)" : change.NewValue;

            // TimestampUtc is populated with paths.StartedUtc = the snapshot's instant = when
            // the import ran. Shown in local time so each user sees "their" date.
            string header = "Changed";
            if (change.TimestampUtc != default)
            {
                var local = change.TimestampUtc.ToLocalTime();
                header = "Changed on " + local.ToString("dd/MM/yyyy HH:mm", culture);
            }

            var sb = new StringBuilder(96);
            sb.Append(header);
            sb.Append('\n').Append("Was: ").Append(oldV);
            sb.Append('\n').Append("Now: ").Append(newV);
            return sb.ToString();
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Cursor converter that flips the cell cursor to <see cref="Cursors.Help"/> when the cell
    /// tracks a change since the last import. Complements the amber fill — the cursor shift is
    /// the standard Windows convention for "hover me for more info".
    /// </summary>
    /// <remarks>
    /// Returns <see cref="DependencyProperty.UnsetValue"/> on unchanged cells so the default
    /// cell cursor is inherited (edit caret, pointer, …) instead of being pinned to an arrow.
    /// </remarks>
    public sealed class CellChangedCursorConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return DependencyProperty.UnsetValue;
            var row = values[0] as ReconciliationViewData;
            var mappingName = values[1] as string;
            if (row == null || string.IsNullOrEmpty(mappingName)) return DependencyProperty.UnsetValue;

            return row.GetChangeForField(mappingName) != null ? Cursors.Help : DependencyProperty.UnsetValue;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
