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
        //                                IMPACT PREVIEW TAB
        // =================================================================================

        // In-memory draft kept for the Impact Preview tab. Never persisted — only consumed by
        // PreviewImpactAsync. Reset whenever the user picks a different rule so a stale edit on
        // rule A can never leak into a preview of rule B.
        private TruthRule _impactDraft;

        private void ImpactRuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Enable the "Edit draft" button only when a rule is selected.
            try { EditImpactDraftButton.IsEnabled = ImpactRuleCombo.SelectedItem is TruthRule; } catch { }

            // Swapping to another rule invalidates any previously held draft.
            if (_impactDraft != null)
            {
                var selectedId = (ImpactRuleCombo.SelectedItem as TruthRule)?.RuleId;
                if (!string.Equals(selectedId, _impactDraft.RuleId, StringComparison.OrdinalIgnoreCase))
                {
                    ClearImpactDraftInternal();
                }
            }
        }

        private void EditImpactDraft_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(ImpactRuleCombo.SelectedItem is TruthRule selected))
                {
                    MessageBox.Show(this, "Pick a rule first.", "Impact Preview", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Seed the editor with the current draft when one exists (so repeated edits stack),
                // otherwise start from the saved rule.
                var seed = _impactDraft != null && string.Equals(_impactDraft.RuleId, selected.RuleId, StringComparison.OrdinalIgnoreCase)
                    ? _impactDraft
                    : selected;

                var editor = new RuleEditorWindow(seed, _offlineFirstService) { Owner = this };
                if (editor.ShowDialog() == true && editor.ResultRule != null)
                {
                    // Keep the draft identity (RuleId) aligned with the selected rule — PreviewImpactAsync
                    // swaps in the draft by matching RuleId.
                    _impactDraft = editor.ResultRule;
                    ClearImpactDraftButton.IsEnabled = true;
                    ImpactDraftStatusText.Text = $"✏️ Draft ready for '{_impactDraft.RuleId}'. Click 'Run Preview' to evaluate impact.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Edit draft failed: {ex.Message}", "Impact Preview", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearImpactDraft_Click(object sender, RoutedEventArgs e) => ClearImpactDraftInternal();

        private void ClearImpactDraftInternal()
        {
            _impactDraft = null;
            try { ClearImpactDraftButton.IsEnabled = false; } catch { }
            try { ImpactDraftStatusText.Text = "No draft — preview will compare the saved rule against itself (no-op). Click 'Edit draft…' to modify a copy."; } catch { }
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

                // Prefer the in-memory draft (if its RuleId still matches the selected rule).
                // Otherwise fall back to the saved rule — this is still useful as a sanity check
                // showing BEFORE == AFTER across all rows.
                var previewTarget = (_impactDraft != null && string.Equals(_impactDraft.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase))
                    ? _impactDraft
                    : rule;

                _cts = new CancellationTokenSource();
                SetBusy(true, _impactDraft != null ? "Running impact preview (draft vs saved)…" : "Running impact preview (saved vs saved — no-op check)…");
                var scope = (ImpactScopeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Edit" ? RuleScope.Edit : RuleScope.Import;
                var progress = BuildProgress();
                var report = await Task.Run(() => _diagnostics.PreviewImpactAsync(previewTarget, scope, progress, _cts.Token)).ConfigureAwait(true);

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
            // Tab order: 0=Coverage, 1=Impact Preview, 2=Proposals, 3=Simulate AMBRE
            MainTabs.SelectedIndex = 1;
            var match = _allRules?.FirstOrDefault(r => string.Equals(r.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase));
            ImpactRuleCombo.SelectedItem = match ?? rule;
        }

        // =================================================================================
        //                                PROPOSALS TAB
        // =================================================================================

        // Full list from the last DB load — kept so the rule-filter combo can slice it client-side
        // without re-hitting T_RuleProposals on every selection change.
        private List<RuleProposal> _loadedProposals = new List<RuleProposal>();
        // Sentinel tag used by the "(all rules)" item in the PropRuleCombo. Any non-null string works
        // as long as no real RuleId can collide with it; we use a value forbidden by the schema.
        private const string PropRuleComboAllTag = "__ALL__";

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
                _loadedProposals = list ?? new List<RuleProposal>();
                RebuildPropRuleCombo();
                ApplyProposalRuleFilter();
                PropStatusText.Text = $"{_loadedProposals.Count} proposal(s) loaded.";
                StatusText.Text = PropStatusText.Text;
            }
            catch (Exception ex)
            {
                PropStatusText.Text = $"Load error: {ex.Message}";
                MessageBox.Show(this, ex.Message, "Proposals", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        /// <summary>
        /// Rebuilds the rule-filter combo from the distinct RuleIds currently in memory.
        /// Preserves the previously selected rule (by string) when it still exists, otherwise
        /// falls back to "(all rules)".
        /// </summary>
        private void RebuildPropRuleCombo()
        {
            try
            {
                var previouslySelected = (PropRuleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                PropRuleCombo.Items.Clear();
                PropRuleCombo.Items.Add(new ComboBoxItem { Content = "(all rules)", Tag = PropRuleComboAllTag });
                foreach (var rid in _loadedProposals
                    .Select(p => p?.RuleId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    PropRuleCombo.Items.Add(new ComboBoxItem { Content = rid, Tag = rid });
                }

                // Restore previous selection if still available.
                int idx = 0;
                if (!string.IsNullOrWhiteSpace(previouslySelected) && previouslySelected != PropRuleComboAllTag)
                {
                    for (int i = 1; i < PropRuleCombo.Items.Count; i++)
                    {
                        var tag = (PropRuleCombo.Items[i] as ComboBoxItem)?.Tag?.ToString();
                        if (string.Equals(tag, previouslySelected, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                    }
                }
                PropRuleCombo.SelectedIndex = idx;
            }
            catch { }
        }

        private void PropRuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PropGrid == null) return;
            ApplyProposalRuleFilter();
        }

        private void ApplyProposalRuleFilter()
        {
            try
            {
                var tag = (PropRuleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                IEnumerable<RuleProposal> view = _loadedProposals;
                if (!string.IsNullOrWhiteSpace(tag) && tag != PropRuleComboAllTag)
                {
                    view = _loadedProposals.Where(p => string.Equals(p?.RuleId, tag, StringComparison.OrdinalIgnoreCase));
                }
                var list = view.ToList();
                PropGrid.ItemsSource = list;
                PropStatusText.Text = $"{list.Count} proposal(s) displayed (of {_loadedProposals.Count} loaded).";
            }
            catch { }
        }

        private void ExportProposals_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Export what's currently displayed (after status + rule filters), not the raw loaded list.
                var items = (PropGrid.ItemsSource as IEnumerable<RuleProposal>)?.ToList() ?? new List<RuleProposal>();
                if (items.Count == 0) { PropStatusText.Text = "Nothing to export."; return; }

                var dlg = new SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv",
                    FileName = $"rule-proposals-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
                };
                if (dlg.ShowDialog(this) != true) return;

                var sb = new StringBuilder();
                sb.AppendLine("ProposalId;Status;RuleId;RecoId;Field;OldValue;NewValue;CreatedAt;CreatedBy;DecidedBy;DecidedAt");
                foreach (var p in items)
                {
                    sb.Append(p.ProposalId?.ToString() ?? string.Empty).Append(';')
                      .Append(Csv(p.Status.ToString())).Append(';')
                      .Append(Csv(p.RuleId)).Append(';')
                      .Append(Csv(p.RecoId)).Append(';')
                      .Append(Csv(p.Field)).Append(';')
                      .Append(Csv(p.OldValue)).Append(';')
                      .Append(Csv(p.NewValue)).Append(';')
                      .Append(p.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                      .Append(Csv(p.CreatedBy)).Append(';')
                      .Append(Csv(p.DecidedBy)).Append(';')
                      .Append(p.DecidedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty)
                      .AppendLine();
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                PropStatusText.Text = $"Exported {items.Count} proposal(s) to {dlg.FileName}";
                StatusText.Text = PropStatusText.Text;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Export failed: {ex.Message}", "Proposals", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
            _loadedProposals = list ?? new List<RuleProposal>();
            // Rebuild the rule combo (distinct RuleIds may have changed after Accept/Reject) and
            // re-apply the currently selected rule filter.
            RebuildPropRuleCombo();
            ApplyProposalRuleFilter();
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
        // Optional: path to a DWINGS snapshot (zip or accdb). When null/empty, the simulation falls
        // back to the country's live DWINGS cache. See ReconciliationService.ApplyDwingsOverride.
        private string _ambreSimDwingsPath;
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

        // ──────────────────────────────────────────────────────────────────────────────────────
        // Optional DWINGS snapshot. User may pick a DW .zip (containing one .accdb) or a .accdb
        // directly; we just stash the path here and DwingsService.LoadFromPathAsync figures out the
        // file type at simulation time. Loading is deferred (zip extraction is expensive) so we do
        // not hit the disk until the user actually clicks Run.
        // ──────────────────────────────────────────────────────────────────────────────────────
        private void PickDwingsFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select DWINGS file (zip containing accdb, or accdb directly)",
                Filter = "DWINGS files (*.zip;*.accdb)|*.zip;*.accdb|All files (*.*)|*.*",
                CheckFileExists = true,
                RestoreDirectory = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                _ambreSimDwingsPath = dlg.FileName;
                DwingsFilePathText.Text = _ambreSimDwingsPath;
                ClearDwingsButton.IsEnabled = true;
                AmbreSimStatusText.Text = "DWINGS snapshot selected. Click Run to simulate with this data.";
            }
        }

        private void ClearDwingsFile_Click(object sender, RoutedEventArgs e)
        {
            _ambreSimDwingsPath = null;
            DwingsFilePathText.Text = "(using country's live DWINGS cache)";
            ClearDwingsButton.IsEnabled = false;
            AmbreSimStatusText.Text = "DWINGS snapshot cleared. Next run will use the live cache.";
        }

        private async void RunAmbreSim_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_ambreSimFilePath) || !File.Exists(_ambreSimFilePath))
            {
                MessageBox.Show(this, "Please pick an AMBRE Excel file first.", "Simulate AMBRE", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!string.IsNullOrWhiteSpace(_ambreSimDwingsPath) && !File.Exists(_ambreSimDwingsPath))
            {
                MessageBox.Show(this, "The selected DWINGS file no longer exists. Please pick another or clear the selection.", "Simulate AMBRE", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    _ambreSimFilePath, countryId, progress, _cts.Token,
                    dwingsFilePath: _ambreSimDwingsPath).ConfigureAwait(true);

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
                RerunSimButton.IsEnabled = RunSimButton.IsEnabled && _ambreSimRows != null && _ambreSimRows.Count > 0;
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

        // ───────────────────────────────────────────────────────────────────────────────────
        // Double-click on a simulated row → opens the rule debug window that shows, for every
        // rule in priority order, whether each condition passed or failed. Re-evaluates against
        // the CURRENT rule set so the user sees the effect of edits made elsewhere without
        // re-reading the Excel file.
        // ───────────────────────────────────────────────────────────────────────────────────
        private async void AmbreSimGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var row = AmbreSimGrid.SelectedItem as RuleSimulationRow;
                if (row == null) return;
                var countryId = _offlineFirstService?.CurrentCountry?.CNT_Id;
                if (string.IsNullOrWhiteSpace(countryId) || _reconciliationService == null) return;

                var (ctx, evaluations) = await _reconciliationService
                    .EvaluateAllRulesForSimulatedRowAsync(row, countryId).ConfigureAwait(true);

                if (ctx == null || evaluations == null || evaluations.Count == 0)
                {
                    MessageBox.Show(this, "Unable to evaluate rules for this row.", "Debug Rules", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var lineInfo = $"ID: {row.ReconciliationId}  |  Account: {row.Account}  |  {(row.IsPivot ? "Pivot" : "Receivable")}  |  Amount: {row.SignedAmount:N2} {row.Currency}";
                var contextInfo = $"IsPivot: {ctx.IsPivot}  |  Country: {ctx.CountryId}  |  " +
                                 $"TransactionType: {ctx.TransactionType ?? "(null)"}  |  " +
                                 $"GuaranteeType: {ctx.GuaranteeType ?? "(null)"}  |  " +
                                 $"IsGrouped: {ctx.IsGrouped?.ToString() ?? "(null)"}  |  " +
                                 $"HasDwingsLink: {ctx.HasDwingsLink?.ToString() ?? "(null)"}";

                var debugItems = new List<RuleDebugItem>();
                int displayOrder = 1;
                foreach (var ev in evaluations)
                {
                    var item = new RuleDebugItem
                    {
                        DisplayOrder = displayOrder++,
                        Rule = ev.Rule,
                        RuleName = ev.Rule?.RuleId ?? "(unnamed)",
                        IsEnabled = ev.IsEnabled,
                        IsMatch = ev.IsMatch,
                        MatchStatus = ev.IsMatch ? "✓ MATCH" : (ev.IsEnabled ? "✗ No Match" : "⊘ Disabled"),
                        OutputAction = ev.Rule?.OutputActionId?.ToString() ?? "-",
                        OutputKPI = ev.Rule?.OutputKpiId?.ToString() ?? "-",
                        Conditions = ev.Conditions?.Select(c => new ConditionDebugItem
                        {
                            Field = c.Field,
                            Expected = c.Expected,
                            Actual = c.Actual,
                            IsMet = c.IsMet,
                            Status = c.IsMet ? "✓" : "✗"
                        }).ToList() ?? new List<ConditionDebugItem>()
                    };
                    debugItems.Add(item);
                }

                var debugWindow = new RuleDebugWindow { Owner = this, Title = $"Rule Debug — {row.ReconciliationId}" };
                debugWindow.SetDebugInfo(lineInfo, contextInfo, debugItems);
                debugWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Debug Rules", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Re-runs the simulation using the current rule set. The TruthTableRepository.RulesChanged
        // event (raised on every rule upsert) has already invalidated the engine's cache, so all we
        // need to do is replay the same file path.
        private void RerunAmbreSim_Click(object sender, RoutedEventArgs e)
        {
            RunAmbreSim_Click(sender, e);
        }
    }
}
