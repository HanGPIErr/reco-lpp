using System;
using System.Collections;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

// NOTE: Same namespace as UserFieldConverters so XAML references don't need updating.
// Physical layout: Windows/ReconciliationView/Converters/*.cs
namespace RecoTool.Windows
{
    /// <summary>
    /// Looks up a display name for an integer id by scanning a sequence of objects exposing
    /// <c>Id</c> and <c>Name</c> properties. Used from <c>RulesAdminWindow</c> to render output
    /// columns (Action / KPI / Incident / Reason) without needing a dedicated repository lookup.
    /// </summary>
    public class IdToOptionNameConverter : IMultiValueConverter
    {
        // values: [0]=id (int or parseable), [1]=IEnumerable of items with Id/Name properties.
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (values == null || values.Length < 2 || values[0] == null) return null;
                if (!(values[1] is IEnumerable seq)) return null;

                int id = ExtractIntId(values[0]);
                return FindOptionNameById(seq, id);
            }
            catch { return null; }
        }

        private static int ExtractIntId(object value)
        {
            if (value is int i) return i;
            if (int.TryParse(value?.ToString(), out var parsed)) return parsed;
            throw new InvalidOperationException("Cannot extract int id from binding value.");
        }

        private static string FindOptionNameById(IEnumerable seq, int targetId)
        {
            foreach (var item in seq)
            {
                if (item == null) continue;
                var t = item.GetType();
                var idProp = t.GetProperty("Id");
                if (idProp == null) continue;

                var rawId = idProp.GetValue(item);
                int itemId;
                if (rawId is int ii) itemId = ii;
                else if (!int.TryParse(rawId?.ToString(), out itemId)) continue;

                if (itemId == targetId)
                {
                    var name = t.GetProperty("Name")?.GetValue(item)?.ToString();
                    return string.IsNullOrWhiteSpace(name) ? item.ToString() : name;
                }
            }
            return null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Maps short guarantee-type codes coming from DWINGS (<c>REISSU</c>, <c>ISSU</c>, <c>NOTIF</c>)
    /// to the user-facing labels shown in the grid. Round-trips for filter-bar ComboBoxes.
    /// </summary>
    public class GuaranteeTypeDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var s = value?.ToString()?.Trim();
                if (string.IsNullOrEmpty(s)) return string.Empty;
                switch (s.ToUpperInvariant())
                {
                    case "REISSU": return "REISSUANCE";
                    case "ISSU":   return "ISSUANCE";
                    case "NOTIF":  return "ADVISING";
                    default:       return s;
                }
            }
            catch { return value?.ToString() ?? string.Empty; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var s = value?.ToString()?.Trim();
                if (string.IsNullOrEmpty(s)) return null;
                switch (s.ToUpperInvariant())
                {
                    case "REISSUANCE": return "REISSU";
                    case "ISSUANCE":   return "ISSU";
                    case "ADVISING":   return "NOTIF";
                    default:           return s;
                }
            }
            catch { return value; }
        }
    }

    /// <summary>Rule columns: P/R/* → friendly labels.</summary>
    public class AccountSideToFriendlyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            switch (s.ToUpperInvariant())
            {
                case "P": return "Pivot";
                case "R": return "Receivable";
                case "*": return "Any (*)";
                default:  return s;
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Rule columns: C/D/* → Credit/Debit/Any.</summary>
    public class SignToFriendlyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            switch (s.ToUpperInvariant())
            {
                case "C": return "Credit";
                case "D": return "Debit";
                case "*": return "Any (*)";
                default:  return s;
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Rule columns: SELF/COUNTERPART/BOTH → friendly.</summary>
    public class ApplyToToFriendlyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            switch (s.ToUpperInvariant())
            {
                case "SELF":        return "Self";
                case "COUNTERPART": return "Counterpart";
                case "BOTH":        return "Both";
                default:            return s;
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Paints the Scope column badge background: Both/Import/Edit → pastel.</summary>
    public class ScopeToBadgeBrushConverter : IValueConverter
    {
        private static readonly Brush BothBrush;
        private static readonly Brush ImportBrush;
        private static readonly Brush EditBrush;

        static ScopeToBadgeBrushConverter()
        {
            var b = new SolidColorBrush(Color.FromArgb(255, 204, 229, 255)); b.Freeze(); BothBrush = b;
            var i = new SolidColorBrush(Color.FromArgb(255, 204, 255, 204)); i.Freeze(); ImportBrush = i;
            var e = new SolidColorBrush(Color.FromArgb(255, 255, 242, 204)); e.Freeze(); EditBrush = e;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return Brushes.Transparent;
            switch (s.ToUpperInvariant())
            {
                case "BOTH":   return BothBrush;
                case "IMPORT": return ImportBrush;
                case "EDIT":   return EditBrush;
                default:       return Brushes.Transparent;
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Paints the Priority column badge. Lower priority = more urgent / redder.
    /// Thresholds are preserved from the legacy implementation to keep visuals stable.
    /// </summary>
    public class PriorityToBadgeBrushConverter : IValueConverter
    {
        private static readonly Brush RedBrush;
        private static readonly Brush OrangeBrush;
        private static readonly Brush YellowBrush;
        private static readonly Brush GreenBrush;
        private static readonly Brush GrayBrush;

        static PriorityToBadgeBrushConverter()
        {
            var r  = new SolidColorBrush(Color.FromArgb(255, 255, 204, 204)); r.Freeze();  RedBrush    = r;
            var o  = new SolidColorBrush(Color.FromArgb(255, 255, 224, 178)); o.Freeze();  OrangeBrush = o;
            var y  = new SolidColorBrush(Color.FromArgb(255, 255, 251, 204)); y.Freeze();  YellowBrush = y;
            var g  = new SolidColorBrush(Color.FromArgb(255, 230, 244, 234)); g.Freeze();  GreenBrush  = g;
            var gr = new SolidColorBrush(Color.FromArgb(255, 240, 240, 240)); gr.Freeze(); GrayBrush   = gr;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                int p = value is int i ? i : (int.TryParse(value?.ToString() ?? "0", out var parsed) ? parsed : 0);
                if (p <= 25)  return RedBrush;
                if (p <= 50)  return OrangeBrush;
                if (p <= 100) return YellowBrush;
                if (p <= 200) return GreenBrush;
                return GrayBrush;
            }
            catch { return Brushes.Transparent; }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Static instances of converters meant to be referenced from XAML via
    /// <c>{x:Static local:UIConverters.&lt;Name&gt;}</c>. Keeps RulesAdminWindow.xaml happy
    /// without per-Window Resource registration.
    /// </summary>
    public static class UIConverters
    {
        public static readonly IValueConverter      ScopeToBadgeBrush    = new ScopeToBadgeBrushConverter();
        public static readonly IValueConverter      PriorityToBadgeBrush = new PriorityToBadgeBrushConverter();
        public static readonly IMultiValueConverter IdToOptionName       = new IdToOptionNameConverter();
        public static readonly IValueConverter      AccountSideToFriendly = new AccountSideToFriendlyConverter();
        public static readonly IValueConverter      SignToFriendly        = new SignToFriendlyConverter();
        public static readonly IValueConverter      ApplyToToFriendly     = new ApplyToToFriendlyConverter();
    }
}
