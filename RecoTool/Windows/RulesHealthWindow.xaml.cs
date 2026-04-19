using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using RecoTool.Services;
using RecoTool.Services.Rules;

namespace RecoTool.Windows
{
    /// <summary>
    /// Rules Health Center — diagnostics & validation hub for the truth-table rules engine.
    /// Four tabs: Coverage / Impact Preview / Proposals / Simulate AMBRE.
    /// Does NOT modify rules or reconciliations; read-only diagnostics.
    /// </summary>
    public partial class RulesHealthWindow : Window
    {
        private readonly ReconciliationService _reconciliationService;
        private readonly OfflineFirstService _offlineFirstService;
        private readonly TruthTableRepository _repository;
        private readonly RulesDiagnosticsService _diagnostics;

        private CancellationTokenSource _cts;

        // Coverage state
        private CoverageReport _lastCoverage;

        // Impact state
        private List<TruthRule> _allRules = new List<TruthRule>();

        public RulesHealthWindow(ReconciliationService reconciliationService, OfflineFirstService offlineFirstService)
        {
            InitializeComponent();
            _reconciliationService = reconciliationService;
            _offlineFirstService = offlineFirstService;
            _repository = new TruthTableRepository(offlineFirstService);
            _diagnostics = new RulesDiagnosticsService(reconciliationService, _repository);

            CountryText.Text = _offlineFirstService?.CurrentCountry?.CNT_Id ?? "—";

            Loaded += async (s, e) => await InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                _allRules = (await _repository.LoadRulesAsync().ConfigureAwait(true)) ?? new List<TruthRule>();
                ImpactRuleCombo.ItemsSource = _allRules.OrderBy(r => r.RuleId).ToList();
            }
            catch (Exception ex) { StatusText.Text = $"Init error: {ex.Message}"; }
        }

        #region Status helpers

        private void SetBusy(bool busy, string message = null)
        {
            CancelBtn.IsEnabled = busy;
            if (!busy) StatusProgress.Value = 0;
            if (!string.IsNullOrEmpty(message)) StatusText.Text = message;
            Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            StatusText.Text = "Cancelled.";
        }

        private IProgress<(int done, int total)> BuildProgress()
        {
            return new Progress<(int done, int total)>(p =>
            {
                if (p.total <= 0) return;
                StatusProgress.Value = (double)p.done / p.total;
                StatusText.Text = $"Processing… {p.done}/{p.total}";
            });
        }

        #endregion

        // Additional handlers appended via partial file (keeps this file readable)
    }
}
