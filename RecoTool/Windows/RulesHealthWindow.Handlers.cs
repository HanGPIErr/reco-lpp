using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RecoTool.Services;
using RecoTool.Services.Rules;

namespace RecoTool.Windows
{
    public partial class RulesHealthWindow
    {
        // =================================================================================
        //                                SIMULATOR TAB
        // =================================================================================

        private RuleScope GetSelectedSimScope()
        {
            var tag = (SimScopeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            return tag == "Edit" ? RuleScope.Edit : tag == "Both" ? RuleScope.Both : RuleScope.Import;
        }

        private async void RunSimulation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_offlineFirstService?.CurrentCountry?.CNT_Id))
                {
                    MessageBox.Show(this, "Please select a country first.", "Rules Health", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _cts = new CancellationTokenSource();
                SetBusy(true, "Collecting rows…");
                var scope = GetSelectedSimScope();
                var progress = BuildProgress();

                var report = await Task.Run(() => _diagnostics.SimulateAsync(scope, progress, _cts.Token)).ConfigureAwait(true);
                _lastSimReport = report;

                _simRows.Clear();
                foreach (var h in report.RuleHits) _simRows.Add(h);

                SimTotalText.Text = report.TotalRows.ToString();
                SimMatchedText.Text = report.MatchedRows.ToString();
                SimUnmatchedText.Text = report.UnmatchedRows.ToString();
                SimDeadText.Text = report.RuleHits.Count(h => h.Applicable && h.Enabled && h.HitCount == 0).ToString();
                SimActiveText.Text = report.RuleHits.Count(h => h.Enabled && h.HitCount > 0).ToString();
                SimRunAtText.Text = $"Simulated at {report.SimulatedAt:yyyy-MM-dd HH:mm:ss}";
                SimSummaryText.Text = report.Summary
                    + (report.UnmatchedRows > 0
                       ? $" — {report.UnmatchedRows} rows would fallback to INVESTIGATE in production."
                       : string.Empty);

                ExportSimBtn.IsEnabled = true;
                StatusText.Text = $"Simulation complete: {report.Summary}";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Simulation cancelled.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Simulation failed: {ex.Message}", "Rules Health", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = $"Error: {ex.Message}";
            }
            finally { SetBusy(false); }
        }

        private bool SimRowFilter(object item)
        {
            if (item is RuleHitStats r)
            {
                var q = SimFilterBox?.Text?.Trim();
                if (string.IsNullOrEmpty(q)) return true;
                return (r.RuleId?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                       || (r.Message?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return true;
        }

        private void SimFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            try { System.Windows.Data.CollectionViewSource.GetDefaultView(SimGrid.ItemsSource)?.Refresh(); } catch { }
        }

        private void SimGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_lastSimReport == null) return;
            if (SimGrid.SelectedItem is RuleHitStats stats && stats.SampleRecoIds != null && stats.SampleRecoIds.Count > 0)
            {
                StatusText.Text = $"Sample reco IDs for {stats.RuleId}: {string.Join(", ", stats.SampleRecoIds.Take(5))}";
            }
        }

        private void ExportSimulation_Click(object sender, RoutedEventArgs e)
        {
            if (_lastSimReport == null) return;
            var dlg = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                FileName = $"rules-simulation-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("RuleId;Priority;Scope;Enabled;AutoApply;Hits;CoveragePercent;LastApplied;SampleRecoIds;Message");
                foreach (var h in _lastSimReport.RuleHits)
                {
                    sb.Append(Csv(h.RuleId)).Append(';')
                      .Append(h.Priority).Append(';')
                      .Append(h.Scope).Append(';')
                      .Append(h.Enabled).Append(';')
                      .Append(h.AutoApply).Append(';')
                      .Append(h.HitCount).Append(';')
                      .Append(h.CoveragePercent.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(';')
                      .Append(h.LastAppliedDisplay).Append(';')
                      .Append(Csv(string.Join("|", h.SampleRecoIds ?? new List<string>()))).Append(';')
                      .Append(Csv(h.Message))
                      .AppendLine();
                }
                sb.AppendLine();
                sb.AppendLine($"# Total rows: {_lastSimReport.TotalRows}");
                sb.AppendLine($"# Matched: {_lastSimReport.MatchedRows}");
                sb.AppendLine($"# Unmatched: {_lastSimReport.UnmatchedRows}");
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                StatusText.Text = $"Exported to {dlg.FileName}";
            }
            catch (Exception ex) { MessageBox.Show(this, $"Export failed: {ex.Message}", "Rules Health", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // =================================================================================
        //                                COVERAGE TAB
        // =================================================================================

        private async void ReloadCoverage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int days = int.TryParse((CovPeriodCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var d) ? d : 30;
                string origin = (CovOriginCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                string country = CovCountryBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(origin)) origin = null;
                if (string.IsNullOrWhiteSpace(country)) country = null;

                SetBusy(true, "Reading logs…");
                var report = await _diagnostics.LoadCoverageAsync(days, origin, country).ConfigureAwait(true);
                _lastCoverage = report;

                CovGrid.ItemsSource = report.PerRule;
                if (report.Warnings != null && report.Warnings.Count > 0)
                {
                    CovWarningsList.ItemsSource = report.Warnings;
                    CovWarningsBox.Visibility = Visibility.Visible;
                }
                else
                {
                    CovWarningsList.ItemsSource = null;
                    CovWarningsBox.Visibility = Visibility.Collapsed;
                }
                ExportCovBtn.IsEnabled = report.PerRule.Count > 0;
                StatusText.Text = $"Coverage loaded: {report.TotalApplications} applications over {days} days ({report.PerRule.Count} distinct rules).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Coverage load failed: {ex.Message}", "Rules Health", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private void ExportCoverage_Click(object sender, RoutedEventArgs e)
        {
            if (_lastCoverage == null) return;
            var dlg = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                FileName = $"rules-coverage-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("RuleId;Total;Import;Edit;RunNow;Other;LastApplied;ByCountry");
                foreach (var s in _lastCoverage.PerRule)
                {
                    sb.Append(Csv(s.RuleId)).Append(';')
                      .Append(s.Total).Append(';')
                      .Append(s.Import).Append(';')
                      .Append(s.Edit).Append(';')
                      .Append(s.RunNow).Append(';')
                      .Append(s.Other).Append(';')
                      .Append(s.LastAppliedDisplay).Append(';')
                      .Append(Csv(s.TopCountry))
                      .AppendLine();
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                StatusText.Text = $"Exported to {dlg.FileName}";
            }
            catch (Exception ex) { MessageBox.Show(this, $"Export failed: {ex.Message}", "Rules Health", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // =================================================================================
        //                                TESTER TAB
        // =================================================================================

        private RuleContext BuildTesterContext()
        {
            var ctx = new RuleContext
            {
                CountryId = TestCountryBox.Text?.Trim(),
                IsPivot = TestIsPivotChk.IsChecked == true,
                GuaranteeType = TesterValueOrNull(TestGuaranteeCombo.Text),
                TransactionType = TesterValueOrNull(TestTransactionCombo.Text),
                HasDwingsLink = TestHasDwingsChk.IsChecked,
                IsGrouped = TestIsGroupedChk.IsChecked,
                IsAmountMatch = TestIsAmountMatchChk.IsChecked,
                Sign = (TestSignCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                MtStatus = TesterValueOrNull((TestMtStatusCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()),
                HasCommIdEmail = TestCommIdEmailChk.IsChecked,
                IsBgiInitiated = TestBgiInitiatedChk.IsChecked,
                PaymentRequestStatus = TesterValueOrNull(TestPaymentStatusBox.Text),
                InvoiceStatus = TesterValueOrNull(TestInvoiceStatusBox.Text),
                IsFirstRequest = TestIsFirstRequestChk.IsChecked,
                IsNewLine = TestIsNewLineChk.IsChecked,
                IsActionDone = TestIsActionDoneChk.IsChecked
            };
            if (int.TryParse(TestDaysSinceReminderBox.Text, out var dsr)) ctx.DaysSinceReminder = dsr;
            if (int.TryParse(TestCurrentActionBox.Text, out var ca)) ctx.CurrentActionId = ca;
            return ctx;
        }

        private static string TesterValueOrNull(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (s.Trim().Equals("(null)", StringComparison.OrdinalIgnoreCase)) return null;
            return s.Trim();
        }

        private async void RunTest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetBusy(true, "Evaluating…");
                var ctx = BuildTesterContext();
                var scopeTag = (TestScopeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                var scope = scopeTag == "Import" ? RuleScope.Import : RuleScope.Edit;

                var results = await _diagnostics.TestAsync(ctx, scope).ConfigureAwait(true);

                // Sort: matching rules first (by Priority), then non-matching
                var ordered = results
                    .OrderByDescending(r => r.IsMatch)
                    .ThenBy(r => r.Rule?.Priority ?? int.MaxValue)
                    .ThenBy(r => r.Rule?.RuleId, StringComparer.OrdinalIgnoreCase)
                    .Select((r, i) => new TesterRow { Order = i + 1, Rule = r.Rule, IsEnabled = r.IsEnabled, IsMatch = r.IsMatch, Conditions = r.Conditions })
                    .ToList();
                TestGrid.ItemsSource = ordered;

                var winner = ordered.FirstOrDefault(r => r.IsMatch);
                if (winner != null)
                {
                    TestHeroBox.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#D5F5E3");
                    TestHeroText.Text = $"✓ Matched: {winner.Rule.RuleId}"
                        + (winner.Rule.OutputActionId.HasValue ? $"  |  Action={winner.Rule.OutputActionId}" : string.Empty)
                        + (winner.Rule.OutputKpiId.HasValue ? $"  |  KPI={winner.Rule.OutputKpiId}" : string.Empty)
                        + (!string.IsNullOrWhiteSpace(winner.Rule.Message) ? $"\nMessage: {winner.Rule.Message}" : string.Empty);
                }
                else
                {
                    TestHeroBox.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#FADBD8");
                    TestHeroText.Text = "✗ No rule matched this context — in production this row would fallback to INVESTIGATE.";
                }
                StatusText.Text = $"Evaluated {results.Count} rules; {ordered.Count(r => r.IsMatch)} match.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Evaluation failed: {ex.Message}", "Rules Health", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private async void LoadContextFromLine_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var id = Microsoft.VisualBasic.Interaction.InputBox("Enter a reconciliation ID to load its context:", "Load context from line", string.Empty);
                if (string.IsNullOrWhiteSpace(id)) return;
                var (ctx, _) = await _reconciliationService.GetRuleDebugInfoAsync(id.Trim()).ConfigureAwait(true);
                if (ctx == null) { StatusText.Text = "Line not found."; return; }
                ApplyContextToTesterForm(ctx);
                StatusText.Text = $"Loaded context from line {id}.";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Load context", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ApplyContextToTesterForm(RuleContext ctx)
        {
            TestCountryBox.Text = ctx.CountryId;
            TestIsPivotChk.IsChecked = ctx.IsPivot;
            TestGuaranteeCombo.Text = ctx.GuaranteeType ?? "(null)";
            TestTransactionCombo.Text = ctx.TransactionType ?? "(null)";
            TestHasDwingsChk.IsChecked = ctx.HasDwingsLink;
            TestIsGroupedChk.IsChecked = ctx.IsGrouped;
            TestIsAmountMatchChk.IsChecked = ctx.IsAmountMatch;
            SelectComboByTag(TestSignCombo, ctx.Sign ?? "");
            SelectComboByTag(TestMtStatusCombo, ctx.MtStatus ?? "");
            TestCommIdEmailChk.IsChecked = ctx.HasCommIdEmail;
            TestBgiInitiatedChk.IsChecked = ctx.IsBgiInitiated;
            TestPaymentStatusBox.Text = ctx.PaymentRequestStatus;
            TestInvoiceStatusBox.Text = ctx.InvoiceStatus;
            TestIsFirstRequestChk.IsChecked = ctx.IsFirstRequest;
            TestIsNewLineChk.IsChecked = ctx.IsNewLine;
            TestDaysSinceReminderBox.Text = ctx.DaysSinceReminder?.ToString() ?? string.Empty;
            TestCurrentActionBox.Text = ctx.CurrentActionId?.ToString() ?? string.Empty;
            TestIsActionDoneChk.IsChecked = ctx.IsActionDone;
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem it in combo.Items)
            {
                if (string.Equals(it.Tag?.ToString() ?? string.Empty, tag ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = it;
                    return;
                }
            }
        }

        private void ResetTesterForm_Click(object sender, RoutedEventArgs e)
        {
            TestCountryBox.Text = _offlineFirstService?.CurrentCountry?.CNT_Id ?? string.Empty;
            TestIsPivotChk.IsChecked = false;
            TestGuaranteeCombo.SelectedIndex = 0;
            TestTransactionCombo.SelectedIndex = 0;
            TestHasDwingsChk.IsChecked = null;
            TestIsGroupedChk.IsChecked = null;
            TestIsAmountMatchChk.IsChecked = null;
            TestSignCombo.SelectedIndex = 0;
            TestMtStatusCombo.SelectedIndex = 0;
            TestCommIdEmailChk.IsChecked = null;
            TestBgiInitiatedChk.IsChecked = null;
            TestPaymentStatusBox.Clear();
            TestInvoiceStatusBox.Clear();
            TestIsFirstRequestChk.IsChecked = null;
            TestIsNewLineChk.IsChecked = null;
            TestDaysSinceReminderBox.Clear();
            TestCurrentActionBox.Clear();
            TestIsActionDoneChk.IsChecked = null;
            TestGrid.ItemsSource = null;
            TestHeroText.Text = "Fill the context and click Evaluate.";
            TestHeroBox.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#ECF0F1");
        }

        // =================================================================================
        //                                IMPACT PREVIEW TAB
        // =================================================================================

        private void ImpactRuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // no-op for now, could preview the rule expression
        }

        private async void RunImpact_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(ImpactRuleCombo.SelectedItem is TruthRule rule))
                {
                    MessageBox.Show(this, "Pick a rule to preview.", "Impact Preview", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _cts = new CancellationTokenSource();
                SetBusy(true, "Running impact preview (double evaluation)…");
                var scope = (ImpactScopeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Edit" ? RuleScope.Edit : RuleScope.Import;
                var progress = BuildProgress();
                var report = await Task.Run(() => _diagnostics.PreviewImpactAsync(rule, scope, progress, _cts.Token)).ConfigureAwait(true);

                ImpactBeforeText.Text = report.BeforeMatchCount.ToString();
                ImpactAfterText.Text = report.AfterMatchCount.ToString();
                ImpactNewText.Text = report.NewlyMatchingIds.Count.ToString();
                ImpactLostText.Text = report.NoLongerMatchingIds.Count.ToString();
                ImpactChangedText.Text = report.ChangedOutputsIds.Count.ToString();
                ImpactNewList.ItemsSource = report.NewlyMatchingIds;
                ImpactLostList.ItemsSource = report.NoLongerMatchingIds;
                ImpactChangedList.ItemsSource = report.ChangedOutputsIds;
                StatusText.Text = $"Preview done: {report.Summary}";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Preview cancelled.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Impact preview failed: {ex.Message}", "Rules Health", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        /// <summary>
        /// Public entry point to open the window pre-focused on the Impact Preview tab, with a rule preselected.
        /// Called by RulesAdminWindow when the user clicks "Preview Impact" on a selected rule.
        /// </summary>
        public void FocusImpactTabForRule(TruthRule rule)
        {
            if (rule == null) return;
            MainTabs.SelectedIndex = 3;
            var match = _allRules?.FirstOrDefault(r => string.Equals(r.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase));
            ImpactRuleCombo.SelectedItem = match ?? rule;
        }

        // =================================================================================
        //                                PROPOSALS TAB
        // =================================================================================

        private async void ReloadProposals_Click(object sender, RoutedEventArgs e)
        {
            // Ignore first SelectionChanged during ComboBox initialization
            if (PropGrid == null) return;
            try
            {
                SetBusy(true, "Loading proposals…");
                var repo = _reconciliationService?.ProposalRepository;
                if (repo == null)
                {
                    PropStatusText.Text = "Proposal repository unavailable.";
                    return;
                }
                var tag = (PropStatusCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                ProposalStatus? filter = null;
                if (!string.IsNullOrWhiteSpace(tag) && Enum.TryParse<ProposalStatus>(tag, true, out var s)) filter = s;

                var list = await repo.LoadAsync(filter).ConfigureAwait(true);
                PropGrid.ItemsSource = list;
                PropStatusText.Text = $"{list.Count} proposal(s) loaded.";
                StatusText.Text = PropStatusText.Text;
            }
            catch (Exception ex)
            {
                PropStatusText.Text = $"Load error: {ex.Message}";
                MessageBox.Show(this, ex.Message, "Proposals", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private async void AcceptProposals_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = PropGrid.SelectedItems?.OfType<RuleProposal>().ToList() ?? new List<RuleProposal>();
                if (selected.Count == 0) { PropStatusText.Text = "No proposal selected."; return; }

                var repo = _reconciliationService?.ProposalRepository;
                if (repo == null) return;

                SetBusy(true, $"Accepting {selected.Count} proposal(s)…");
                int accepted = 0, applied = 0;
                var user = Environment.UserName;

                // Group by RecoId so all proposals on one row are applied together
                foreach (var group in selected.Where(p => p.ProposalId.HasValue).GroupBy(p => p.RecoId, StringComparer.OrdinalIgnoreCase))
                {
                    var recoId = group.Key;
                    if (string.IsNullOrWhiteSpace(recoId)) continue;

                    RecoTool.Models.Reconciliation reco = null;
                    try { reco = await _reconciliationService.GetOrCreateReconciliationAsync(recoId).ConfigureAwait(true); }
                    catch { }

                    foreach (var p in group)
                    {
                        try
                        {
                            // Mark accepted first
                            await repo.UpdateStatusAsync(p.ProposalId.Value, ProposalStatus.Accepted, user).ConfigureAwait(true);
                            accepted++;

                            if (reco == null) continue;
                            if (ApplyProposalToReconciliation(p, reco))
                            {
                                // Try to persist the mutation
                                try
                                {
                                    await _reconciliationService.SaveReconciliationAsync(reco, applyRulesOnEdit: false).ConfigureAwait(true);
                                    await repo.UpdateStatusAsync(p.ProposalId.Value, ProposalStatus.Applied, user).ConfigureAwait(true);
                                    applied++;
                                }
                                catch (Exception saveEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Proposal {p.ProposalId} accepted but save failed: {saveEx.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Accept proposal {p.ProposalId} failed: {ex.Message}");
                        }
                    }
                }

                PropStatusText.Text = $"{accepted} accepted, {applied} applied to reconciliations.";
                StatusText.Text = PropStatusText.Text;
                await ReloadProposalsAsync().ConfigureAwait(true);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Accept proposals", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { SetBusy(false); }
        }

        private async void RejectProposals_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = PropGrid.SelectedItems?.OfType<RuleProposal>().ToList() ?? new List<RuleProposal>();
                if (selected.Count == 0) { PropStatusText.Text = "No proposal selected."; return; }

                var repo = _reconciliationService?.ProposalRepository;
                if (repo == null) return;

                SetBusy(true, $"Rejecting {selected.Count} proposal(s)…");
                int rejected = 0;
                var user = Environment.UserName;

                foreach (var p in selected)
                {
                    if (!p.ProposalId.HasValue) continue;
                    try
                    {
                        await repo.UpdateStatusAsync(p.ProposalId.Value, ProposalStatus.Rejected, user).ConfigureAwait(true);
                        rejected++;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Reject proposal {p.ProposalId} failed: {ex.Message}");
                    }
                }

                PropStatusText.Text = $"{rejected} proposal(s) rejected.";
                StatusText.Text = PropStatusText.Text;
                await ReloadProposalsAsync().ConfigureAwait(true);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Reject proposals", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { SetBusy(false); }
        }

        private async Task ReloadProposalsAsync()
        {
            var repo = _reconciliationService?.ProposalRepository;
            if (repo == null) return;
            var tag = (PropStatusCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            ProposalStatus? filter = null;
            if (!string.IsNullOrWhiteSpace(tag) && Enum.TryParse<ProposalStatus>(tag, true, out var s)) filter = s;
            var list = await repo.LoadAsync(filter).ConfigureAwait(true);
            PropGrid.ItemsSource = list;
            PropStatusText.Text = $"{list.Count} proposal(s) loaded.";
        }

        /// <summary>
        /// Tries to apply one proposal's NewValue to the corresponding field of the reconciliation.
        /// Returns true if the reconciliation was modified (so caller should save).
        /// </summary>
        private static bool ApplyProposalToReconciliation(RuleProposal p, RecoTool.Models.Reconciliation reco)
        {
            if (p == null || reco == null) return false;
            if (string.IsNullOrWhiteSpace(p.Field)) return false;

            bool TryParseInt(string s, out int v) => int.TryParse(s, out v);
            bool TryParseBool(string s, out bool v) => bool.TryParse(s, out v);
            bool TryParseDate(string s, out DateTime v) => DateTime.TryParse(s, out v);

            switch (p.Field)
            {
                case "Action":
                    if (TryParseInt(p.NewValue, out var ai) && reco.Action != ai) { reco.Action = ai; return true; }
                    break;
                case "ActionStatus":
                    if (TryParseBool(p.NewValue, out var asv) && reco.ActionStatus != asv) { reco.ActionStatus = asv; reco.ActionDate = DateTime.Now; return true; }
                    break;
                case "KPI":
                    if (TryParseInt(p.NewValue, out var ki) && reco.KPI != ki) { reco.KPI = ki; return true; }
                    break;
                case "IncidentType":
                    if (TryParseInt(p.NewValue, out var ii) && reco.IncidentType != ii) { reco.IncidentType = ii; return true; }
                    break;
                case "RiskyItem":
                    if (TryParseBool(p.NewValue, out var ri) && reco.RiskyItem != ri) { reco.RiskyItem = ri; return true; }
                    break;
                case "ReasonNonRisky":
                    if (TryParseInt(p.NewValue, out var rn) && reco.ReasonNonRisky != rn) { reco.ReasonNonRisky = rn; return true; }
                    break;
                case "ToRemind":
                    if (TryParseBool(p.NewValue, out var tr) && reco.ToRemind != tr) { reco.ToRemind = tr; return true; }
                    break;
                case "ToRemindDate":
                    if (TryParseDate(p.NewValue, out var trd) && reco.ToRemindDate != trd) { reco.ToRemindDate = trd; return true; }
                    break;
                case "FirstClaimDate":
                    if (TryParseDate(p.NewValue, out var fcd) && reco.FirstClaimDate != fcd) { reco.FirstClaimDate = fcd; return true; }
                    break;
                case "LastClaimDate":
                    if (TryParseDate(p.NewValue, out var lcd) && reco.LastClaimDate != lcd) { reco.LastClaimDate = lcd; return true; }
                    break;
            }
            return false;
        }

        // =================================================================================
        //                          AMBRE IMPORT SIMULATION TAB
        // =================================================================================

        private string _ambreSimFilePath;
        private List<RuleSimulationRow> _ambreSimRows = new List<RuleSimulationRow>();

        private void PickAmbreFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select AMBRE Excel file to simulate",
                Filter = "Excel files (*.xlsx;*.xlsm;*.xls)|*.xlsx;*.xlsm;*.xls|All files (*.*)|*.*",
                CheckFileExists = true,
                RestoreDirectory = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                _ambreSimFilePath = dlg.FileName;
                AmbreFilePathText.Text = _ambreSimFilePath;
                RunSimButton.IsEnabled = true;
                AmbreSimStatusText.Text = "File selected. Click Run.";
            }
        }

        private async void RunAmbreSim_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_ambreSimFilePath) || !File.Exists(_ambreSimFilePath))
            {
                MessageBox.Show(this, "Please pick an AMBRE Excel file first.", "Simulate AMBRE", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var countryId = _offlineFirstService?.CurrentCountry?.CNT_Id;
            if (string.IsNullOrWhiteSpace(countryId))
            {
                MessageBox.Show(this, "Please select a country first.", "Simulate AMBRE", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_reconciliationService == null) return;

            try
            {
                SetBusy(true, "Starting simulation…");
                RunSimButton.IsEnabled = false;
                _ambreSimRows = new List<RuleSimulationRow>();
                AmbreSimGrid.ItemsSource = null;

                var progress = new Progress<(string message, int percent)>(t =>
                {
                    try { AmbreSimStatusText.Text = $"{t.message} ({t.percent}%)"; StatusText.Text = AmbreSimStatusText.Text; } catch { }
                });

                _cts = new CancellationTokenSource();
                var rows = await _reconciliationService.SimulateAmbreImportFromFileAsync(
                    _ambreSimFilePath, countryId, progress, _cts.Token).ConfigureAwait(true);

                _ambreSimRows = rows ?? new List<RuleSimulationRow>();

                // --- KPIs ---
                int total = _ambreSimRows.Count;
                int matched = _ambreSimRows.Count(r => !string.IsNullOrWhiteSpace(r.MatchedRuleId));
                int mutate = _ambreSimRows.Count(r => r.WouldMutate);
                int newRows = _ambreSimRows.Count(r => !r.ExistsInDb);
                int uniqueRules = _ambreSimRows.Where(r => !string.IsNullOrWhiteSpace(r.MatchedRuleId)).Select(r => r.MatchedRuleId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                AmbreSimTotal.Text = total.ToString();
                AmbreSimMatched.Text = matched.ToString();
                AmbreSimMutate.Text = mutate.ToString();
                AmbreSimNew.Text = newRows.ToString();
                AmbreSimUniqueRules.Text = uniqueRules.ToString();

                // --- Rule filter combo ---
                try
                {
                    var ruleIds = _ambreSimRows
                        .Where(r => !string.IsNullOrWhiteSpace(r.MatchedRuleId))
                        .Select(r => r.MatchedRuleId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x)
                        .ToList();
                    AmbreSimRuleFilter.Items.Clear();
                    AmbreSimRuleFilter.Items.Add("(all rules)");
                    foreach (var r in ruleIds) AmbreSimRuleFilter.Items.Add(r);
                    AmbreSimRuleFilter.SelectedIndex = 0;
                }
                catch { }

                ApplyAmbreSimFilter();
                AmbreSimStatusText.Text = $"Done. {total} rows, {matched} matched, {mutate} would mutate.";
                StatusText.Text = AmbreSimStatusText.Text;
            }
            catch (Exception ex)
            {
                AmbreSimStatusText.Text = $"Error: {ex.Message}";
                MessageBox.Show(this, ex.Message, "Simulate AMBRE", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
                RunSimButton.IsEnabled = !string.IsNullOrWhiteSpace(_ambreSimFilePath);
            }
        }

        private void AmbreSimFilter_Changed(object sender, RoutedEventArgs e)
        {
            ApplyAmbreSimFilter();
        }

        private void ApplyAmbreSimFilter()
        {
            try
            {
                if (_ambreSimRows == null) return;
                IEnumerable<RuleSimulationRow> q = _ambreSimRows;
                if (AmbreSimFilterMutate?.IsChecked == true) q = q.Where(r => r.WouldMutate);
                if (AmbreSimFilterMatched?.IsChecked == true) q = q.Where(r => !string.IsNullOrWhiteSpace(r.MatchedRuleId));
                if (AmbreSimFilterNew?.IsChecked == true) q = q.Where(r => !r.ExistsInDb);
                var ruleFilter = AmbreSimRuleFilter?.SelectedItem as string;
                if (!string.IsNullOrEmpty(ruleFilter) && !string.Equals(ruleFilter, "(all rules)", StringComparison.Ordinal))
                    q = q.Where(r => string.Equals(r.MatchedRuleId, ruleFilter, StringComparison.OrdinalIgnoreCase));
                // Sort: mutations first, then matched, then by rule
                var list = q.OrderByDescending(r => r.WouldMutate)
                            .ThenByDescending(r => !string.IsNullOrWhiteSpace(r.MatchedRuleId))
                            .ThenBy(r => r.MatchedRuleId ?? string.Empty)
                            .ToList();
                AmbreSimGrid.ItemsSource = list;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AmbreSim] filter error: {ex.Message}");
            }
        }

        private void ExportAmbreSim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_ambreSimRows == null || _ambreSimRows.Count == 0)
                {
                    MessageBox.Show(this, "Nothing to export — run a simulation first.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var dlg = new SaveFileDialog
                {
                    Title = "Export simulation results",
                    Filter = "CSV (*.csv)|*.csv",
                    FileName = $"ambre_sim_{DateTime.Now:yyyyMMdd_HHmm}.csv",
                    RestoreDirectory = true
                };
                if (dlg.ShowDialog(this) != true) return;

                var sb = new StringBuilder();
                sb.AppendLine("WouldMutate;ExistsInDb;IsPivot;ID;Account;CCY;SignedAmount;MatchedRuleId;Priority;ChangesSummary;UserMessage;RawLabel");
                foreach (var r in _ambreSimRows)
                {
                    string Csv(object v) => v == null ? string.Empty : ("\"" + v.ToString().Replace("\"", "\"\"") + "\"");
                    sb.Append(r.WouldMutate); sb.Append(';');
                    sb.Append(r.ExistsInDb); sb.Append(';');
                    sb.Append(r.IsPivot); sb.Append(';');
                    sb.Append(Csv(r.ReconciliationId)); sb.Append(';');
                    sb.Append(Csv(r.Account)); sb.Append(';');
                    sb.Append(Csv(r.Currency)); sb.Append(';');
                    sb.Append(r.SignedAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty); sb.Append(';');
                    sb.Append(Csv(r.MatchedRuleId)); sb.Append(';');
                    sb.Append(r.MatchedRulePriority?.ToString() ?? string.Empty); sb.Append(';');
                    sb.Append(Csv(r.ChangesSummary)); sb.Append(';');
                    sb.Append(Csv(r.UserMessage)); sb.Append(';');
                    sb.Append(Csv(r.RawLabel));
                    sb.AppendLine();
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                AmbreSimStatusText.Text = $"Exported {_ambreSimRows.Count} rows to {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>View-model row for the Tester grid.</summary>
    internal class TesterRow
    {
        public int Order { get; set; }
        public TruthRule Rule { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsMatch { get; set; }
        public List<RuleConditionDebug> Conditions { get; set; }
    }
}
