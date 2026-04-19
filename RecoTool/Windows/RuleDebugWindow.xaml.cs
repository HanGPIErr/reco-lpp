using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using RecoTool.Services;
using RecoTool.Services.Rules;

namespace RecoTool.Windows
{
    public partial class RuleDebugWindow : Window
    {
        public RuleDebugWindow()
        {
            InitializeComponent();
        }

        public void SetDebugInfo(string lineInfo, string contextInfo, List<RuleDebugItem> rules)
        {
            LineInfoText.Text = lineInfo;
            ContextInfoText.Text = contextInfo;
            RulesDataGrid.ItemsSource = rules;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ──────────────────────────────────────────────────────────────────────────────────────
        // Double-click a rule → open the full rule editor, pre-populated with a clone of the
        // clicked rule. On OK we persist via TruthTableRepository; RulesChanged (raised by the
        // repository) invalidates the engine's rule cache so the next re-evaluation sees the
        // edit. We deliberately do NOT re-run the evaluation here: the caller built the debug
        // items from a specific reconciliation row and we don't carry its RuleContext, so the
        // user closes this window and double-clicks the AMBRE row again to see the fresh result.
        // ──────────────────────────────────────────────────────────────────────────────────────
        private async void RulesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var item = RulesDataGrid.SelectedItem as RuleDebugItem;
                if (item?.Rule == null) return;

                // Resolve services from the app container — same pattern as RulesAdminWindow.
                var offlineSvc = App.ServiceProvider?.GetService(typeof(OfflineFirstService)) as OfflineFirstService;
                if (offlineSvc == null)
                {
                    MessageBox.Show(this, "OfflineFirstService not available.", "Edit Rule", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Clone so that a canceled edit doesn't corrupt the snapshot currently displayed.
                var seed = item.Rule.Clone();
                var editor = new RuleEditorWindow(seed, offlineSvc) { Owner = this };
                if (editor.ShowDialog() != true || editor.ResultRule == null)
                    return;

                var repo = new TruthTableRepository(offlineSvc);
                var ok = await repo.UpsertRuleAsync(editor.ResultRule).ConfigureAwait(true);
                if (!ok)
                {
                    MessageBox.Show(this, "Failed to save rule.", "Edit Rule", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                MessageBox.Show(this,
                    $"Rule '{editor.ResultRule.RuleId}' saved.\n\nClose this window and double-click the AMBRE row again to re-evaluate with the updated rule.",
                    "Rule saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Edit Rule", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// Represents a rule with its evaluation result for debug display
    /// </summary>
    public class RuleDebugItem
    {
        public int DisplayOrder { get; set; }
        public int RuleId { get; set; }
        public string RuleName { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsMatch { get; set; }
        public string MatchStatus { get; set; }
        public string OutputAction { get; set; }
        public string OutputKPI { get; set; }
        public List<ConditionDebugItem> Conditions { get; set; } = new List<ConditionDebugItem>();
        /// <summary>
        /// Reference to the underlying rule. Used by the debug window so the user can double-click
        /// a row to open the rule editor directly. May be null if the debug item was built from an
        /// orphaned/unknown rule.
        /// </summary>
        public TruthRule Rule { get; set; }
    }

    /// <summary>
    /// Represents a single condition with its evaluation result
    /// </summary>
    public class ConditionDebugItem
    {
        public string Field { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
        public bool IsMet { get; set; }
        public string Status { get; set; } // "✓" or "✗"
    }
}
