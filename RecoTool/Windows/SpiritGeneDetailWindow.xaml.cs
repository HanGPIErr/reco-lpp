using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using RecoTool.API;

namespace RecoTool.Windows
{
    public partial class SpiritGeneDetailWindow : Window
    {
        public SpiritGeneDetailWindow(SpiritGeneTransactionDetailOutput.GDetOpe detail, string transactionId)
        {
            InitializeComponent();

            TitleText.Text = $"Transaction {transactionId}";
            SubTitleText.Text = $"MsgId: {detail?.EndToEndId ?? "—"}  |  Type: {detail?.TypeOpe ?? "—"}  |  CSM: {detail?.Csm ?? "—"}";

            FieldsControl.ItemsSource = BuildFields(detail);
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

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            var items = FieldsControl.ItemsSource as IEnumerable<DetailField>;
            if (items == null) return;

            var sb = new StringBuilder();
            foreach (var f in items)
                sb.AppendLine($"{f.Label}: {f.Value}");

            Clipboard.SetText(sb.ToString());
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
}
