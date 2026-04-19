using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RecoTool.Services;
using RecoTool.Services.DTOs;
using RecoTool.Services.Helpers;
using RecoTool.Infrastructure.Logging;
using RecoTool.Services.Rules;
using Syncfusion.UI.Xaml.Grid.Helpers;
using Syncfusion.UI.Xaml.Grid;

namespace RecoTool.Windows
{
    // Partial: Editing handlers and save plumbing
    public partial class ReconciliationView
    {
        // Prevent double invocation of confirmation when multiple handlers fire
        private bool _ruleConfirmBusy;
        
        // Debounce checkbox saves to avoid multiple saves for the same row
        private readonly Dictionary<string, System.Threading.CancellationTokenSource> _checkboxSavePending = new Dictionary<string, System.Threading.CancellationTokenSource>();

        // ── Archived-row write guard ─────────────────────────────────────────────────────────
        // Archived (IsDeleted) rows are effectively read-only. Comments are the single
        // exception — the business explicitly wants to keep annotating settled items, e.g. to
        // log post-mortem notes or to route an @mention to another user. Every mutation path
        // below funnels through IsFieldLockedByArchive so the rule lives in one place.
        private static readonly HashSet<string> _archivedEditableFields = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase) { "Comments", "LastComment" };

        // Re-entrancy flag for the checkbox revert path so setting IsChecked back doesn't
        // trigger CheckBox_CheckedChanged a second time (which would otherwise save on the revert).
        private bool _revertingArchivedCheckbox;

        // Rate-limit the "you can't edit this row" toast so the user sees it once per row
        // they attack instead of getting spammed on every keystroke.
        private string _lastArchivedToastRowId;

        /// <summary>
        /// Returns <c>true</c> when <paramref name="row"/> is archived AND <paramref name="mappingName"/>
        /// is not in the whitelist of fields we still let through (Comments). Unknown mapping names
        /// are treated as locked — unsafe-by-default so a new editable column doesn't accidentally
        /// leak past the guard without an explicit review here.
        /// </summary>
        private static bool IsFieldLockedByArchive(ReconciliationViewData row, string mappingName)
        {
            if (row == null || !row.IsDeleted) return false;
            if (string.IsNullOrEmpty(mappingName)) return true;
            return !_archivedEditableFields.Contains(mappingName);
        }

        /// <summary>
        /// Shows a one-shot toast when the user keeps trying to edit a locked archived row.
        /// Deduped per row id so rapid key/click bursts don't stack notifications.
        /// </summary>
        private void NotifyArchivedRowBlocked(ReconciliationViewData row)
        {
            try
            {
                var rid = row?.ID ?? string.Empty;
                if (string.Equals(_lastArchivedToastRowId, rid, StringComparison.OrdinalIgnoreCase)) return;
                _lastArchivedToastRowId = rid;
                ShowToast("🔒 Archived row — only comments can be edited.");
            }
            catch { }
        }
        private async void CheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                // Short-circuit the re-entrant notification fired by our own revert write below.
                if (_revertingArchivedCheckbox) return;

                var checkBox = sender as CheckBox;
                if (checkBox == null) return;
                
                // CRITICAL: Ignore programmatic changes during virtualization/scroll
                // Only process user-initiated clicks
                if (!checkBox.IsMouseOver && !checkBox.IsKeyboardFocusWithin)
                {
                    //System.Diagnostics.Debug.WriteLine("[CheckBox_CheckedChanged] IGNORED: Not user-initiated (virtualization/scroll)");
                    return;
                }
                
                var row = checkBox.DataContext as ReconciliationViewData;
                if (row == null || string.IsNullOrEmpty(row.ID)) return;

                // No checkbox column is in the whitelist, so any archived row click is blocked.
                // Flip IsChecked back to undo the TwoWay update the click just pushed onto the DTO
                // — otherwise the checkbox visual would stay on while the DB stays off.
                if (row.IsDeleted)
                {
                    _revertingArchivedCheckbox = true;
                    try { checkBox.IsChecked = !(checkBox.IsChecked ?? false); }
                    finally { _revertingArchivedCheckbox = false; }
                    NotifyArchivedRowBlocked(row);
                    return;
                }
                
                // Cancel any pending save for this row
                if (_checkboxSavePending.TryGetValue(row.ID, out var existingCts))
                {
                    existingCts?.Cancel();
                    _checkboxSavePending.Remove(row.ID);
                }
                
                // Create a new cancellation token for this save
                var cts = new System.Threading.CancellationTokenSource();
                _checkboxSavePending[row.ID] = cts;
                
                // Wait a short delay to debounce rapid clicks
                await Task.Delay(300, cts.Token);
                
                // Remove from pending dictionary
                _checkboxSavePending.Remove(row.ID);
                
                // Save the row
                await SaveEditedRowAsync(row);
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                // Debounced - another save is coming
            }
            catch (Exception ex)
            {
                
            }
        }
        
        // Persist selection changes for Action/KPI/Incident and ActionStatus
        private async void UserFieldComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                var cb = sender as ComboBox;
                if (cb == null) return;

                // Ignore events fired during ComboBox initialization (no added items)
                // or when the same item is re-selected (added == removed)
                if (e.AddedItems.Count == 0) return;
                if (e.AddedItems.Count == 1 && e.RemovedItems.Count == 1
                    && Equals(e.AddedItems[0], e.RemovedItems[0])) return;

                // Ignore programmatic / virtualization events: only react when dropdown was open
                if (!cb.IsDropDownOpen && !cb.IsKeyboardFocusWithin) return;

                var row = cb.DataContext as ReconciliationViewData;
                if (row == null) return;

                // None of the ComboBox popups (Action / KPI / IncidentType / Risky / ActionStatus)
                // are whitelisted on archived rows — revert the pick and bail.
                if (row.IsDeleted)
                {
                    try
                    {
                        if (e.RemovedItems != null && e.RemovedItems.Count == 1)
                            cb.SelectedItem = e.RemovedItems[0];
                        else
                            cb.SelectedItem = null;
                    }
                    catch { }
                    NotifyArchivedRowBlocked(row);
                    return;
                }

                // Determine which field changed via Tag
                var tag = cb.Tag as string;
                int? newId = null;
                // Accept explicit null to clear selection; otherwise attempt to convert to int?
                try
                {
                    if (cb.SelectedValue == null)
                    {
                        newId = null;
                    }
                    else if (cb.SelectedValue is int directInt)
                    {
                        newId = directInt;
                    }
                    else if (int.TryParse(cb.SelectedValue.ToString(), out var parsed))
                    {
                        newId = parsed;
                    }
                    else
                    {
                        // If we cannot parse, treat as clear
                        newId = null;
                    }
                }
                catch
                {
                    newId = null;
                }

                // Load current reconciliation from DB to avoid overwriting unrelated fields
                if (_reconciliationService == null) return;
                var reco = await _reconciliationService.GetOrCreateReconciliationAsync(row.ID);

                if (string.Equals(tag, "Action", StringComparison.OrdinalIgnoreCase))
                {
                    UserFieldUpdateService.ApplyAction(row, reco, newId, AllUserFields);
                    ViewDataEnricher.RefreshActionDisplay(row);

                    // Si l'action est TRIGGER, propager à toutes les lignes du groupe
                    if (newId == (int)ActionType.Trigger)
                    {
                        await PropagateTriggerToGroupAsync(row);
                    }
                }
                else if (string.Equals(tag, "ActionStatus", StringComparison.OrdinalIgnoreCase))
                {
                    // Toggle pending/done directly; auto manage ActionDate only if status actually changes
                    bool? newStatus = null;
                    if (cb.SelectedValue is bool sb) newStatus = sb;
                    else if (cb.SelectedValue != null && bool.TryParse(cb.SelectedValue.ToString(), out var parsedBool)) newStatus = parsedBool;

                    UserFieldUpdateService.ApplyActionStatus(row, reco, newStatus);
                    ViewDataEnricher.RefreshActionDisplay(row);
                }
                else if (string.Equals(tag, "KPI", StringComparison.OrdinalIgnoreCase))
                {
                    UserFieldUpdateService.ApplyKpi(row, reco, newId);
                    ViewDataEnricher.RefreshUserFieldDisplay(row, "KPI");
                }
                else if (string.Equals(tag, "Incident Type", StringComparison.OrdinalIgnoreCase) || string.Equals(tag, "IncidentType", StringComparison.OrdinalIgnoreCase))
                {
                    UserFieldUpdateService.ApplyIncidentType(row, reco, newId);
                    ViewDataEnricher.RefreshIncidentDisplay(row);
                }
                else if (string.Equals(tag, "ReasonNonRisky", StringComparison.OrdinalIgnoreCase) || string.Equals(tag, "Reason Non Risky", StringComparison.OrdinalIgnoreCase))
                {
                    row.ReasonNonRisky = newId;
                    reco.ReasonNonRisky = newId;
                    ViewDataEnricher.RefreshReasonNonRiskyDisplay(row);
                }
                else
                {
                    return;
                }

                // Stamp the user-edit protection for this field (context-menu / combobox edit).
                // Normalise label to match the field names used by RuleApplicationHelper (e.g. "Incident Type" -> "IncidentType").
                try
                {
                    string normalisedField = tag;
                    if (string.Equals(tag, "Incident Type", StringComparison.OrdinalIgnoreCase)) normalisedField = "IncidentType";
                    else if (string.Equals(tag, "Reason Non Risky", StringComparison.OrdinalIgnoreCase)) normalisedField = "ReasonNonRisky";
                    RecoTool.Services.Rules.RuleApplicationHelper.StampUserEdit(reco, normalisedField);
                }
                catch { }

                // Preview rules for edit and ask confirmation if self outputs are proposed
                var ruleApplied = await ConfirmAndApplyRuleOutputsAsync(row, reco, tag);

                // Save without applying rules again (we already applied selected outputs above)
                // Only save if user confirmed rule application or if no rule was proposed
                if (ruleApplied)
                {
                    await _reconciliationService.SaveReconciliationAsync(reco, applyRulesOnEdit: false);
                }
                StampRowsModified(new[] { row });
                
                // Refresh KPIs to reflect changes immediately
                UpdateKpis(_filteredData);

                // Fire-and-forget background sync to network DB to reduce sync debt (debounced)
                try
                {
                    ScheduleBulkPushDebounced();
                }
                catch { /* ignore any scheduling errors */ }
                try { RefreshActivityLog(); } catch { }
            }
            catch (Exception ex)
            {
                // Enrichir le message avec des infos de diagnostic de connexion
                try
                {
                    string country = _offlineFirstService?.CurrentCountryId ?? "<null>";
                    bool isInit = _offlineFirstService?.IsInitialized ?? false;
                    string cs = null;
                    try { cs = _offlineFirstService?.GetCurrentLocalConnectionString(); } catch (Exception csex) { cs = $"<error: {csex.Message}>"; }
                    string dw = null;
                    try { dw = _offlineFirstService?.GetLocalDWDatabasePath(); } catch { }
                    ShowError($"Save error: {ex.Message}\nCountry: {country} | Init: {isInit}\nCS: {cs}\nDW: {dw}");
                }
                catch
                {
                    ShowError($"Save error: {ex.Message}");
                }
            }
        }

        // Persist text/checkbox/date edits as soon as a cell commit occurs (SfDataGrid version)
        private async void ResultsDataGrid_CurrentCellEndEdit(object sender, Syncfusion.UI.Xaml.Grid.CurrentCellEndEditEventArgs e)
        {
            try
            {
                var grid = sender as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (grid == null) return;

                // Get row data from the record index
                var rowData = grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) as ReconciliationViewData;
                if (rowData == null) return;

                // Determine which column was edited
                int columnIndex = grid.ResolveToGridVisibleColumnIndex(e.RowColumnIndex.ColumnIndex);
                if (columnIndex < 0 || columnIndex >= grid.Columns.Count) return;
                var column = grid.Columns[columnIndex];
                var mappingName = column.MappingName ?? string.Empty;

                // Skip ComboBox-based columns handled by UserFieldComboBox_SelectionChanged
                if (string.Equals(mappingName, "Action", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mappingName, "KPI", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mappingName, "IncidentType", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mappingName, "ReasonNonRisky", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Defence-in-depth: CurrentCellBeginEdit cancels locked edits first, but older
                // Syncfusion paths occasionally skip BeginEdit for programmatic commits.
                if (IsFieldLockedByArchive(rowData, mappingName))
                {
                    NotifyArchivedRowBlocked(rowData);
                    return;
                }

                // CRITICAL: SfDataGrid fires CurrentCellEndEdit BEFORE updating the binding source.
                // Capture the new value from the focused TextBox and write it to the row property
                // immediately so that SaveEditedRowAsync sees the new value instead of the old one
                // (which would cause DB change detection to NOOP).
                try
                {
                    if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox focusedTb
                        && !string.IsNullOrEmpty(mappingName))
                    {
                        var newCellValue = focusedTb.Text;
                        var prop = rowData.GetType().GetProperty(mappingName);
                        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
                        {
                            var oldVal = prop.GetValue(rowData) as string;
                            if (!string.Equals(oldVal, newCellValue, StringComparison.Ordinal))
                            {
                                prop.SetValue(rowData, newCellValue);
                                System.Diagnostics.Debug.WriteLine($"[CellEndEdit] Forced {mappingName}: '{oldVal}' -> '{newCellValue}'");
                            }
                        }
                    }
                }
                catch { }

                // Defer save to allow any remaining binding updates to complete
                Dispatcher?.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await SaveEditedRowAsync(rowData);
                    }
                    catch (Exception ex)
                    {
                        ShowError($"Save error (deferred): {ex.Message}");
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                ShowError($"Save error (cell): {ex.Message}");
            }
        }

        // Persist checkbox toggles (ToRemind / ACK / RiskyItem).
        // PERF/BUG: GridCheckBoxColumn does NOT fire CurrentCellEndEdit when EditTrigger="OnDoubleTap"
        // because a single click on a checkbox just flips the bound property via TwoWay binding
        // without entering/leaving the grid's edit mode. CurrentCellValueChanged DOES fire in that
        // case, so we rely on it — but ONLY for checkbox columns, otherwise we'd double-save the
        // text/date edits already handled by CurrentCellEndEdit.
        private void ResultsDataGrid_CurrentCellValueChanged(object sender, Syncfusion.UI.Xaml.Grid.CurrentCellValueChangedEventArgs e)
        {
            try
            {
                var grid = sender as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (grid == null) return;

                int colIdx = grid.ResolveToGridVisibleColumnIndex(e.RowColumnIndex.ColumnIndex);
                if (colIdx < 0 || colIdx >= grid.Columns.Count) return;
                var column = grid.Columns[colIdx];
                if (!(column is Syncfusion.UI.Xaml.Grid.GridCheckBoxColumn)) return;

                var rowData = grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) as ReconciliationViewData;
                if (rowData == null) return;

                // No checkbox column is whitelisted for archived rows. Block the save path here
                // and rely on CheckBox_CheckedChanged (for templated CheckBoxes) + the DTO
                // setters to roll back the visual state.
                if (IsFieldLockedByArchive(rowData, column.MappingName ?? string.Empty))
                {
                    NotifyArchivedRowBlocked(rowData);
                    return;
                }

                // Defer to let the binding source finish updating before we read it.
                Dispatcher?.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await SaveEditedRowAsync(rowData);
                    }
                    catch (Exception ex)
                    {
                        ShowError($"Save error (checkbox): {ex.Message}");
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                ShowError($"Save error (checkbox handler): {ex.Message}");
            }
        }

        /// <summary>
        /// Cancels cell-level editing before it starts when the row is archived and the target
        /// column isn't whitelisted (only Comments / LastComment pass). This is the <b>primary</b>
        /// protection — it prevents the cell from entering edit mode at all, so the user sees the
        /// "can't edit" toast instead of a half-committed edit. CurrentCellEndEdit + the per-
        /// handler guards below are fallbacks for paths where BeginEdit is skipped (programmatic
        /// commits, native check-box toggles).
        /// </summary>
        private void ResultsDataGrid_CurrentCellBeginEdit(object sender, Syncfusion.UI.Xaml.Grid.CurrentCellBeginEditEventArgs e)
        {
            try
            {
                var grid = sender as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (grid == null) return;

                var rowData = grid.GetRecordAtRowIndex(e.RowColumnIndex.RowIndex) as ReconciliationViewData;
                if (rowData == null || !rowData.IsDeleted) return;

                int colIdx = grid.ResolveToGridVisibleColumnIndex(e.RowColumnIndex.ColumnIndex);
                if (colIdx < 0 || colIdx >= grid.Columns.Count) return;

                var mapping = grid.Columns[colIdx]?.MappingName ?? string.Empty;
                if (IsFieldLockedByArchive(rowData, mapping))
                {
                    e.Cancel = true;
                    NotifyArchivedRowBlocked(rowData);
                }
            }
            catch { /* swallow — never block navigation on a guard failure */ }
        }

        // Loads existing reconciliation and maps editable fields from the view row, then saves.
        // Final safety net for the archived-row guard: when the caller has slipped past all the
        // upstream checks, we fold the save to a Comments-only update so the rest of the row stays
        // exactly as it sits on disk.
        private async Task SaveEditedRowAsync(ReconciliationViewData row)
        {
            if (_reconciliationService == null || row == null) return;
            var reco = await _reconciliationService.GetOrCreateReconciliationAsync(row.ID);

            // Archived rows — only Comments can change. Short-circuit to a minimal save path so
            // no stale field from the view DTO overwrites the persisted record.
            if (row.IsDeleted)
            {
                if (!string.Equals(reco.Comments, row.Comments, StringComparison.Ordinal))
                {
                    reco.Comments = row.Comments;
                    await _reconciliationService.SaveReconciliationAsync(reco, applyRulesOnEdit: false);
                    StampRowsModified(new[] { row });
                    try { ScheduleBulkPushDebounced(); } catch { }
                    try { RefreshActivityLog(); } catch { }
                }
                return;
            }

            // Track if linking fields changed (to know if we need to recalculate grouping)
            var oldInternalRef = reco.InternalInvoiceReference;
            var oldDwingsInvoice = reco.DWINGS_InvoiceID;
            var oldDwingsGuarantee = reco.DWINGS_GuaranteeID;

            // Detect which user-editable fields actually changed, so we can stamp UserEditedFields
            // to protect them from future silent rule overwrites.
            var userChangedFields = new System.Collections.Generic.List<string>();
            if (reco.Action != row.Action) userChangedFields.Add("Action");
            if (reco.ActionStatus != row.ActionStatus) userChangedFields.Add("ActionStatus");
            if (reco.KPI != row.KPI) userChangedFields.Add("KPI");
            if (reco.IncidentType != row.IncidentType) userChangedFields.Add("IncidentType");
            if (reco.RiskyItem != row.RiskyItem) userChangedFields.Add("RiskyItem");
            if (reco.ReasonNonRisky != row.ReasonNonRisky) userChangedFields.Add("ReasonNonRisky");
            if (reco.ToRemind != row.ToRemind) userChangedFields.Add("ToRemind");
            if (reco.ToRemindDate != row.ToRemindDate) userChangedFields.Add("ToRemindDate");
            if (reco.FirstClaimDate != row.FirstClaimDate) userChangedFields.Add("FirstClaimDate");
            if (reco.LastClaimDate != row.LastClaimDate) userChangedFields.Add("LastClaimDate");
            if (!string.Equals(reco.Assignee, row.Assignee, StringComparison.Ordinal)) userChangedFields.Add("Assignee");

            // Map user-editable fields
            reco.Action = row.Action;
            reco.ActionStatus = row.ActionStatus;
            reco.ActionDate = row.ActionDate;
            reco.KPI = row.KPI;
            reco.IncidentType = row.IncidentType;
            reco.Comments = row.Comments;
            reco.InternalInvoiceReference = row.InternalInvoiceReference;
            reco.FirstClaimDate = row.FirstClaimDate;
            reco.LastClaimDate = row.LastClaimDate;
            // Persist assignee if edited via grid
            reco.Assignee = row.Assignee;
            reco.ToRemind = row.ToRemind;
            reco.ToRemindDate = row.ToRemindDate;
            reco.ACK = row.ACK;
            reco.SwiftCode = row.SwiftCode;
            reco.PaymentReference = row.PaymentReference;
            reco.RiskyItem = row.RiskyItem;
            reco.ReasonNonRisky = row.ReasonNonRisky;
            reco.IncNumber = row.IncNumber;
            // Persist Free API / DWINGS enrichment fields
            reco.MbawData = row.MbawData;
            reco.SpiritData = row.SpiritData;
            reco.DWINGS_GuaranteeID = row.DWINGS_GuaranteeID;
            reco.DWINGS_InvoiceID = row.DWINGS_InvoiceID;
            reco.DWINGS_BGPMT = row.DWINGS_BGPMT;

            // Stamp user-edit protection: these fields cannot be silently overwritten by rules
            // (honouring RespectUserEdits + UserEditLockDays on each rule).
            if (userChangedFields.Count > 0)
            {
                RecoTool.Services.Rules.RuleApplicationHelper.StampUserEdit(reco, userChangedFields.ToArray());
            }

            // Check if linking fields actually changed OR if they have a value (even if unchanged)
            // We need to recalculate when:
            // 1. The value changed (old != new)
            // 2. A value was added to an empty field (null -> "something")
            // 3. The field has a value that could link to other rows
            bool linkingFieldsChanged = !string.Equals(oldInternalRef, reco.InternalInvoiceReference, StringComparison.OrdinalIgnoreCase)
                                     || !string.Equals(oldDwingsInvoice, reco.DWINGS_InvoiceID, StringComparison.OrdinalIgnoreCase);
            
            bool dwingsLinksChanged = !string.Equals(oldDwingsInvoice, reco.DWINGS_InvoiceID, StringComparison.OrdinalIgnoreCase)
                                   || !string.Equals(oldDwingsGuarantee, reco.DWINGS_GuaranteeID, StringComparison.OrdinalIgnoreCase);
            
            bool hasLinkingValue = !string.IsNullOrWhiteSpace(reco.InternalInvoiceReference) 
                                 || !string.IsNullOrWhiteSpace(reco.DWINGS_InvoiceID);
            
            // CRITICAL: If user changed DWINGS links, refresh all DWINGS-derived properties
            if (dwingsLinksChanged)
            {
                row.RefreshDwingsData();
            }

            // Always save user-edited fields first (IncNumber, Comments, etc.)
            await _reconciliationService.SaveReconciliationAsync(reco, applyRulesOnEdit: false);

            // Then preview rules and apply rule-proposed outputs on top if confirmed
            var editField = linkingFieldsChanged ? "Linking" : (string)null;
            var ruleApplied = await ConfirmAndApplyRuleOutputsAsync(row, reco, editField);
            if (ruleApplied)
            {
                // Save again only if rule outputs were applied on top of the manual edit
                await _reconciliationService.SaveReconciliationAsync(reco, applyRulesOnEdit: false);
            }

            // Recalculate grouping if linking fields changed
            if (linkingFieldsChanged || hasLinkingValue)
            {
                try
                {
                    var changedRef = reco.InternalInvoiceReference ?? reco.DWINGS_InvoiceID;
                    if (!string.IsNullOrWhiteSpace(changedRef))
                    {
                        RecalculateGroupingFlagsIncremental(changedRef);
                    }
                    else
                    {
                        RecalculateGroupingFlags();
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"RecalculateGroupingFlags failed: {ex.Message}");
                }
            }
            
            // Update the row in the UI data sources to reflect the saved changes
            // This ensures checkbox edits (ACK, RiskyItem, etc.) are immediately visible
            try
            {
                // Find and update the row in _allViewData
                var allRow = _allViewData?.FirstOrDefault(x => string.Equals(x.ID, row.ID, StringComparison.OrdinalIgnoreCase));
                if (allRow != null && allRow != row)
                {
                    // Copy all fields from the edited row to ensure consistency
                    allRow.ACK = row.ACK;
                    allRow.RiskyItem = row.RiskyItem;
                    allRow.Action = row.Action;
                    allRow.ActionStatus = row.ActionStatus;
                    allRow.ActionDate = row.ActionDate;
                    allRow.KPI = row.KPI;
                    allRow.IncidentType = row.IncidentType;
                    allRow.Comments = row.Comments;
                    allRow.InternalInvoiceReference = row.InternalInvoiceReference;
                    allRow.FirstClaimDate = row.FirstClaimDate;
                    allRow.LastClaimDate = row.LastClaimDate;
                    allRow.Assignee = row.Assignee;
                    allRow.ToRemind = row.ToRemind;
                    allRow.ToRemindDate = row.ToRemindDate;
                    allRow.SwiftCode = row.SwiftCode;
                    allRow.PaymentReference = row.PaymentReference;
                    allRow.ReasonNonRisky = row.ReasonNonRisky;
                    allRow.IncNumber = row.IncNumber;
                    allRow.MbawData = row.MbawData;
                    allRow.SpiritData = row.SpiritData;
                    allRow.DWINGS_GuaranteeID = row.DWINGS_GuaranteeID;
                    allRow.DWINGS_InvoiceID = row.DWINGS_InvoiceID;
                    allRow.DWINGS_BGPMT = row.DWINGS_BGPMT;
                }
            }
            catch { }

            StampRowsModified(new[] { row });

            // Refresh KPIs to reflect changes immediately
            UpdateKpis(_filteredData);

            // Best-effort background sync (debounced)
            try
            {
                ScheduleBulkPushDebounced();
            }
            catch { }
            try { RefreshActivityLog(); } catch { }
        }
        
        /// <summary>
        /// Recalculates IsMatchedAcrossAccounts (flag "G") and MissingAmount for a specific group only
        /// OPTIMIZED: 95% faster than full recalculation (10ms vs 200ms)
        /// </summary>
        private void RecalculateGroupingFlagsIncremental(string changedInvoiceRef)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(changedInvoiceRef)) return;
                
                var allData = _allViewData;
                if (allData == null || allData.Count == 0) return;
                
                var country = CurrentCountryObject;
                if (country == null) return;
                
                // Use the optimized incremental recalculation
                ReconciliationViewEnricher.RecalculateFlagsForGroup(
                    allData,
                    changedInvoiceRef,
                    country.CNT_AmbreReceivable,
                    country.CNT_AmbrePivot
                );
            }
            catch (Exception ex)
            {
                throw new Exception($"RecalculateGroupingFlagsIncremental failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Recalculates IsMatchedAcrossAccounts (flag "G") and MissingAmount after manual edits
        /// This mirrors the logic in ReconciliationService but works on the current in-memory dataset
        /// FULL RECALCULATION: Use only when necessary (e.g., filter change, data reload)
        /// </summary>
        private void RecalculateGroupingFlags()
        {
            try
            {
                // CRITICAL: Use _allViewData (all rows) instead of VM.ViewData (filtered rows only)
                // This ensures we see both Pivot and Receivable rows even when filtering by Account_ID
                var allData = _allViewData;
                if (allData == null || allData.Count == 0) return;
                
                var country = CurrentCountryObject;
                if (country == null) return;
                
                // IMPORTANT: Do NOT reset all flags! This would erase flags from other groups.
                // Instead, recalculate for ALL groups and let the logic set the correct values.
                
                // First, reset flags for rows that will be recalculated
                var rowsToRecalculate = new HashSet<ReconciliationViewData>();
                
                // Recalculate IsMatchedAcrossAccounts (flag "G")
                // Group by DWINGS_InvoiceID first
                var byInvoice = allData.Where(r => !string.IsNullOrWhiteSpace(r.DWINGS_InvoiceID))
                                       .GroupBy(r => r.DWINGS_InvoiceID, StringComparer.OrdinalIgnoreCase);
                foreach (var g in byInvoice)
                {
                    bool hasP = g.Any(x => string.Equals(x.AccountSide, "P", StringComparison.OrdinalIgnoreCase));
                    bool hasR = g.Any(x => string.Equals(x.AccountSide, "R", StringComparison.OrdinalIgnoreCase));
                    
                    foreach (var row in g)
                    {
                        rowsToRecalculate.Add(row);
                        row.IsMatchedAcrossAccounts = hasP && hasR;
                    }
                }
                
                // Group by InternalInvoiceReference (INDEPENDENTLY - can coexist with DWINGS_InvoiceID)
                // This allows a line to belong to multiple groups (BGI group + Internal ref group)
                var byInternal = allData.Where(r => !string.IsNullOrWhiteSpace(r.InternalInvoiceReference))
                                        .GroupBy(r => r.InternalInvoiceReference, StringComparer.OrdinalIgnoreCase);
                foreach (var g in byInternal)
                {
                    bool hasP = g.Any(x => string.Equals(x.AccountSide, "P", StringComparison.OrdinalIgnoreCase));
                    bool hasR = g.Any(x => string.Equals(x.AccountSide, "R", StringComparison.OrdinalIgnoreCase));
                    
                    // If grouped by InternalInvoiceReference, mark as matched
                    // This will override or combine with DWINGS_InvoiceID grouping
                    if (hasP && hasR)
                    {
                        foreach (var row in g)
                        {
                            rowsToRecalculate.Add(row);
                            row.IsMatchedAcrossAccounts = true; // Set to true if matched in this group
                        }
                    }
                }
                
                // Reset flags for ungrouped rows (no DWINGS_InvoiceID and no InternalInvoiceReference)
                foreach (var row in allData)
                {
                    if (!rowsToRecalculate.Contains(row))
                    {
                        row.IsMatchedAcrossAccounts = false;
                        row.MissingAmount = null;
                        row.CounterpartTotalAmount = null;
                        row.CounterpartCount = null;
                    }
                }
                
                // Recalculate MissingAmount using the enricher
                ReconciliationViewEnricher.CalculateMissingAmounts(
                    allData.ToList(), 
                    country.CNT_AmbreReceivable, 
                    country.CNT_AmbrePivot
                );
                
                // Refresh pre-calculated display caches for all affected rows
                foreach (var row in rowsToRecalculate)
                    row.PreCalculateDisplayProperties();
            }
            catch (Exception ex)
            {
                throw new Exception($"RecalculateGroupingFlags failed: {ex.Message}");
            }
        }

        // Resolve a user-field display name by id and category
        private string GetUserFieldName(int? id, string category)
        {
            try
            {
                if (!id.HasValue) return null;
                var list = AllUserFields;
                if (list == null) return id.Value.ToString();
                var q = list.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(category))
                    q = q.Where(u => string.Equals(u?.USR_Category ?? string.Empty, category, StringComparison.OrdinalIgnoreCase));
                var uf = q.FirstOrDefault(u => u?.USR_ID == id.Value) ?? list.FirstOrDefault(u => u?.USR_ID == id.Value);
                return uf?.USR_FieldName ?? id.Value.ToString();
            }
            catch { return id?.ToString(); }
        }

        // Display rule outputs and apply to current row upon confirmation; notify counterpart outputs via toast
        // Returns true if changes were applied and should be saved, false otherwise
        private async Task<bool> ConfirmAndApplyRuleOutputsAsync(ReconciliationViewData row, RecoTool.Models.Reconciliation reco, string editedField = null)
        {
            try
            {
                if (_ruleConfirmBusy) return true; // Allow save to proceed if busy
                _ruleConfirmBusy = true;
                
                try
                {
                    var res = await _reconciliationService.PreviewRulesForEditAsync(row.ID, editedField);
                    if (res == null || res.Rule == null) return true; // No rule, allow save
                    
                    // DEBUG: Log rule evaluation
                    System.Diagnostics.Debug.WriteLine($"[RuleConfirm] Rule '{res.Rule.RuleId}' triggered for row {row.ID}");
                    System.Diagnostics.Debug.WriteLine($"  AutoApply={res.Rule.AutoApply}, RequiresUserConfirm={res.RequiresUserConfirm}");

                    // Prepare SELF outputs summary (labels, not IDs) with emojis
                    var selfChanges = new List<string>();
                    if (res.NewActionIdSelf.HasValue) selfChanges.Add($"⚡ Action: {GetUserFieldName(res.NewActionIdSelf.Value, "Action")}\u200B");
                    if (res.NewKpiIdSelf.HasValue) selfChanges.Add($"📊 KPI: {GetUserFieldName(res.NewKpiIdSelf.Value, "KPI")}\u200B");
                    if (res.NewIncidentTypeIdSelf.HasValue) selfChanges.Add($"🔔 Incident Type: {GetUserFieldName(res.NewIncidentTypeIdSelf.Value, "Incident Type")}\u200B");
                    if (res.NewRiskyItemSelf.HasValue) selfChanges.Add($"⚠️ Risky Item: {(res.NewRiskyItemSelf.Value ? "Yes" : "No")}\u200B");
                    if (res.NewActionStatusSelf.HasValue) selfChanges.Add($"✅ Action Status: {(res.NewActionStatusSelf.Value ? "DONE" : "PENDING")}\u200B");
                    if (res.NewReasonNonRiskyIdSelf.HasValue) selfChanges.Add($"✅ Reason Non Risky: {GetUserFieldName(res.NewReasonNonRiskyIdSelf.Value, "Reason Non Risky")}");
                    if (res.NewToRemindSelf.HasValue) selfChanges.Add($"🔔 To Remind: {(res.NewToRemindSelf.Value ? "Yes" : "No")}");
                    if (res.NewToRemindDaysSelf.HasValue) selfChanges.Add($"📅 To Remind Days: {res.NewToRemindDaysSelf.Value}");
                    if (res.NewFirstClaimTodaySelf == true) selfChanges.Add("📅 First Claim Date: Today");

                    // Show counterpart toast if rule applies to counterpart
                    if (res.Rule.ApplyTo == ApplyTarget.Counterpart || res.Rule.ApplyTo == ApplyTarget.Both)
                    {
                        var cp = new List<string>();
                        if (res.Rule.OutputActionId.HasValue) cp.Add($"⚡ Action: {GetUserFieldName(res.Rule.OutputActionId.Value, "Action")}");
                        if (res.Rule.OutputKpiId.HasValue) cp.Add($"📊 KPI: {GetUserFieldName(res.Rule.OutputKpiId.Value, "KPI")}");
                        if (res.Rule.OutputIncidentTypeId.HasValue) cp.Add($"🔔 Incident: {GetUserFieldName(res.Rule.OutputIncidentTypeId.Value, "Incident Type")}");
                        if (res.Rule.OutputRiskyItem.HasValue) cp.Add($"⚠️ Risky: {(res.Rule.OutputRiskyItem.Value ? "Yes" : "No")}");
                        if (res.Rule.OutputReasonNonRiskyId.HasValue) cp.Add($"✅ Reason: {GetUserFieldName(res.Rule.OutputReasonNonRiskyId.Value, "Reason Non Risky")}");
                        if (res.Rule.OutputToRemind.HasValue) cp.Add($"🔔 Remind: {(res.Rule.OutputToRemind.Value ? "Yes" : "No")}");
                        if (res.Rule.OutputToRemindDays.HasValue) cp.Add($"📅 Days: {res.Rule.OutputToRemindDays.Value}");
                        var txt = cp.Count > 0 ? string.Join(", ", cp) : "(no change)";
                        var ruleTitleX = !string.IsNullOrWhiteSpace(res.Rule.RuleId) ? res.Rule.RuleId : "Rule";
                        try { ShowToast($"🔄 {ruleTitleX} → Counterpart: {txt}", onClick: () => { try { OpenMatchedPopup(row); } catch { } }); } catch { }
                    }

                    // DEBUG: Log detected changes
                    System.Diagnostics.Debug.WriteLine($"  SelfChanges count: {selfChanges.Count}");
                    foreach (var change in selfChanges)
                    {
                        System.Diagnostics.Debug.WriteLine($"    - {change}");
                    }
                    
                    if (selfChanges.Count == 0)
                    {
                        // Nothing to confirm/apply on current row, allow save to proceed
                        System.Diagnostics.Debug.WriteLine($"  → No SELF changes, skipping confirmation");
                        return true;
                    }
                    
                    // Check if rule requires user confirmation (AutoApply=false or explicit flag)
                    if (res.Rule.AutoApply && !res.RequiresUserConfirm)
                    {
                        // Auto-apply without confirmation
                        System.Diagnostics.Debug.WriteLine($"  → AutoApply=true, applying without confirmation");
                        // Apply changes directly (code moved below)
                    }
                    else
                    {
                        // Show confirmation dialog
                        System.Diagnostics.Debug.WriteLine($"  → Showing confirmation dialog");
                        var details = string.Join("\n   ", selfChanges);
                        var ruleTitle = !string.IsNullOrWhiteSpace(res.Rule.RuleId) ? res.Rule.RuleId : "Unnamed Rule";
                        var userMsg = !string.IsNullOrWhiteSpace(res.UserMessage) ? $"\n\n💬 Message: {res.UserMessage}" : "";
                        var msgText = $"🎯 Rule '{ruleTitle}' proposes to apply:\n\n   {details}{userMsg}\n\nDo you want to apply these changes?";
                        var answer = MessageBox.Show(msgText, "✨ Confirm Rule Application", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (answer != MessageBoxResult.Yes)
                        {
                            System.Diagnostics.Debug.WriteLine($"  → User declined");
                            return false; // User declined, don't save
                        }
                        System.Diagnostics.Debug.WriteLine($"  → User accepted");
                    }

                    // Apply to in-memory row for instant UI feedback
                    if (res.NewActionIdSelf.HasValue) { UserFieldUpdateService.ApplyAction(row, reco, res.NewActionIdSelf.Value, AllUserFields); }
                    if (res.NewActionStatusSelf.HasValue) { UserFieldUpdateService.ApplyActionStatus(row, reco, res.NewActionStatusSelf.Value); }
                    if (res.NewKpiIdSelf.HasValue) { row.KPI = res.NewKpiIdSelf.Value; reco.KPI = res.NewKpiIdSelf.Value; }
                    if (res.NewIncidentTypeIdSelf.HasValue) { row.IncidentType = res.NewIncidentTypeIdSelf.Value; reco.IncidentType = res.NewIncidentTypeIdSelf.Value; }
                    if (res.NewRiskyItemSelf.HasValue) { row.RiskyItem = res.NewRiskyItemSelf.Value; reco.RiskyItem = res.NewRiskyItemSelf.Value; }
                    if (res.NewReasonNonRiskyIdSelf.HasValue) { row.ReasonNonRisky = res.NewReasonNonRiskyIdSelf.Value; reco.ReasonNonRisky = res.NewReasonNonRiskyIdSelf.Value; }
                    if (res.NewToRemindSelf.HasValue) { row.ToRemind = res.NewToRemindSelf.Value; reco.ToRemind = res.NewToRemindSelf.Value; }
                    if (res.NewToRemindDaysSelf.HasValue)
                    {
                        try { var d = DateTime.Today.AddDays(res.NewToRemindDaysSelf.Value); row.ToRemindDate = d; reco.ToRemindDate = d; } catch { }
                    }
                    if (res.NewFirstClaimTodaySelf == true)
                    {
                        try
                        {
                            if (reco.FirstClaimDate.HasValue)
                            {
                                row.LastClaimDate = DateTime.Today; reco.LastClaimDate = DateTime.Today;
                            }
                            else
                            {
                                row.FirstClaimDate = DateTime.Today; reco.FirstClaimDate = DateTime.Today;
                            }
                        }
                        catch { }
                    }
                    
                    // Apply UserMessage to Comments if present
                    if (!string.IsNullOrWhiteSpace(res.UserMessage))
                    {
                        try
                        {
                            var currentUser = ResolveUserDisplayName(_reconciliationService?.CurrentUser ?? Environment.UserName);
                            var prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {currentUser}: ";
                            var msg = prefix + $"[Rule {res.Rule.RuleId ?? "(unnamed)"}] {res.UserMessage}";
                            if (string.IsNullOrWhiteSpace(row.Comments))
                            {
                                row.Comments = msg;
                                reco.Comments = msg;
                            }
                            else if (!row.Comments.Contains(msg))
                            {
                                row.Comments = msg + Environment.NewLine + row.Comments;
                                reco.Comments = msg + Environment.NewLine + reco.Comments;
                            }
                        }
                        catch { }
                    }

                    // Log file entry (origin=edit) to preserve trace
                    try
                    {
                        var outs = new List<string>();
                        if (res.NewActionIdSelf.HasValue) outs.Add($"Action={res.NewActionIdSelf.Value}");
                        if (res.NewActionStatusSelf.HasValue) outs.Add($"ActionStatus={(res.NewActionStatusSelf.Value ? "DONE" : "PENDING")} ");
                        if (res.NewKpiIdSelf.HasValue) outs.Add($"KPI={res.NewKpiIdSelf.Value}");
                        if (res.NewIncidentTypeIdSelf.HasValue) outs.Add($"IncidentType={res.NewIncidentTypeIdSelf.Value}");
                        if (res.NewRiskyItemSelf.HasValue) outs.Add($"RiskyItem={res.NewRiskyItemSelf.Value}");
                        if (res.NewReasonNonRiskyIdSelf.HasValue) outs.Add($"ReasonNonRisky={res.NewReasonNonRiskyIdSelf.Value}");
                        if (res.NewToRemindSelf.HasValue) outs.Add($"ToRemind={res.NewToRemindSelf.Value}");
                        if (res.NewToRemindDaysSelf.HasValue) outs.Add($"ToRemindDays={res.NewToRemindDaysSelf.Value}");
                        if (res.NewFirstClaimTodaySelf == true) outs.Add("FirstClaimDate=Today");
                        var outsStr = string.Join("; ", outs);
                        var countryId = _offlineFirstService?.CurrentCountryId;
                        LogHelper.WriteRuleApplied("edit", countryId, row.ID, res.Rule.RuleId, outsStr, res.UserMessage);
                    }
                    catch { }
                    
                    return true; // Changes applied successfully, allow save
                }
                finally
                {
                    _ruleConfirmBusy = false;
                }
            }
            catch { return true; } // On error, allow save to proceed
        }
        /// <summary>
        /// Propage l'action TRIGGER à toutes les lignes du même groupe.
        /// Un groupe = lignes partageant le même BGI (DWINGS_GuaranteeID), BGPMT (DWINGS_BGPMT) ou InternalInvoiceRef.
        /// Toutes les lignes du groupe passent en TRIGGER PENDING (ActionStatus = false).
        /// </summary>
        private async Task PropagateTriggerToGroupAsync(ReconciliationViewData sourceRow)
        {
            try
            {
                if (_reconciliationService == null || _offlineFirstService == null) return;
                if (sourceRow == null || string.IsNullOrWhiteSpace(sourceRow.ID)) return;

                // Collecter les clés de groupement de la ligne source
                var groupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // BGI = DWINGS_GuaranteeID
                if (!string.IsNullOrWhiteSpace(sourceRow.DWINGS_GuaranteeID))
                    groupKeys.Add($"BGI:{sourceRow.DWINGS_GuaranteeID}");

                // BGPMT = DWINGS_BGPMT
                if (!string.IsNullOrWhiteSpace(sourceRow.DWINGS_BGPMT))
                    groupKeys.Add($"BGPMT:{sourceRow.DWINGS_BGPMT}");

                // InternalInvoiceReference
                if (!string.IsNullOrWhiteSpace(sourceRow.InternalInvoiceReference))
                    groupKeys.Add($"INTREF:{sourceRow.InternalInvoiceReference}");

                if (groupKeys.Count == 0)
                {
                    // Pas de clé de groupement, rien à propager
                    return;
                }

                // Trouver toutes les lignes du groupe dans les données en mémoire
                var relatedRows = new List<ReconciliationViewData>();
                var allData = _allViewData;
                if (allData == null || allData.Count == 0) return;

                foreach (var row in allData)
                {
                    // Skip la ligne source
                    if (string.Equals(row.ID, sourceRow.ID, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip les lignes archivées (IsDeleted = true)
                    if (row.IsDeleted)
                        continue;

                    bool isRelated = false;

                    // Vérifier BGI (DWINGS_GuaranteeID)
                    if (!string.IsNullOrWhiteSpace(row.DWINGS_GuaranteeID) && groupKeys.Contains($"BGI:{row.DWINGS_GuaranteeID}"))
                        isRelated = true;

                    // Vérifier BGPMT
                    if (!string.IsNullOrWhiteSpace(row.DWINGS_BGPMT) && groupKeys.Contains($"BGPMT:{row.DWINGS_BGPMT}"))
                        isRelated = true;

                    // Vérifier InternalInvoiceReference
                    if (!string.IsNullOrWhiteSpace(row.InternalInvoiceReference) && groupKeys.Contains($"INTREF:{row.InternalInvoiceReference}"))
                        isRelated = true;

                    if (isRelated)
                        relatedRows.Add(row);
                }

                if (relatedRows.Count == 0)
                {
                    // Pas de lignes liées
                    return;
                }

                // Mettre à jour les lignes liées en mémoire et en base
                var triggerActionId = (int)ActionType.Trigger;
                var now = DateTime.Now;
                var updatedCount = 0;

                foreach (var row in relatedRows)
                {
                    // Mettre à jour en mémoire (UI)
                    row.Action = triggerActionId;
                    row.ActionStatus = false; // PENDING
                    row.ActionDate = now;

                    // Mettre à jour en base
                    try
                    {
                        var reco = await _reconciliationService.GetOrCreateReconciliationAsync(row.ID);
                        reco.Action = triggerActionId;
                        reco.ActionStatus = false; // PENDING
                        reco.ActionDate = now;
                        await _reconciliationService.SaveReconciliationAsync(reco, applyRulesOnEdit: false);
                        updatedCount++;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PropagateTrigger] Failed to update row {row.ID}: {ex.Message}");
                    }
                }

                if (updatedCount > 0)
                {
                    // Afficher un toast pour informer l'utilisateur
                    ShowToast($"🔄 TRIGGER propagé à {updatedCount} ligne(s) du groupe");

                    // Rafraîchir les KPIs
                    UpdateKpis(_filteredData);

                    // Planifier la synchronisation
                    ScheduleBulkPushDebounced();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PropagateTrigger] Error: {ex.Message}");
            }
        }
        // ── Popup editing for date/Assignee/UserField columns (converted to GridTextColumn for scroll perf) ──
        private static readonly HashSet<string> _datePopupColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ActionDate", "FirstClaimDate", "LastClaimDate", "ToRemindDate"
        };

        // mapping name → UserFieldOptionsConverter category parameter
        private static readonly Dictionary<string, string> _userFieldPopupColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Action", "Action" },
            { "KPI", "KPI" },
            { "IncidentType", "Incident Type" },
            { "ReasonNonRisky", "RISKY" }
        };

        // Shared converter instance — the cache it holds is static anyway, but sharing avoids
        // allocating a new converter + interface vtable per popup open.
        private static readonly UserFieldOptionsConverter _sharedUserFieldConverter = new UserFieldOptionsConverter();

        // Popup infrastructure for inline cell editing.
        // PERF: the Popup/Border instances are created once and reused across all editors
        // (saves HWND + layered-window + DropShadowEffect D3D surface allocation per open).
        // BUG FIX: _cellEditInner tracks the ComboBox/DatePicker so CloseEditPopup can close its
        // dropdown BEFORE the outer popup is dismissed — otherwise the dropdown's own top-level
        // popup window becomes orphaned (stays visible until the next click).
        private Popup _cellEditPopup;
        private Border _cellEditBorder;
        private FrameworkElement _cellEditInner;

        private void ResultsDataGrid_CellTapped(object sender, Syncfusion.UI.Xaml.Grid.GridCellTappedEventArgs e)
        {
            try
            {
                var grid = sender as SfDataGrid;
                if (grid == null) return;
                var row = e.Record as ReconciliationViewData;
                if (row == null) return;
                var col = e.Column;
                if (col == null) return;
                var mapping = col.MappingName ?? "";

                // Archived rows: only the Comments dialog + the read-only Mbaw preview pass
                // through. Everything else (date pickers, Assignee, ActionStatus, UserField
                // popups) would mutate a row that's supposed to be frozen.
                if (IsFieldLockedByArchive(row, mapping)
                    && !string.Equals(mapping, "MbawData", StringComparison.OrdinalIgnoreCase))
                {
                    NotifyArchivedRowBlocked(row);
                    return;
                }

                if (_datePopupColumns.Contains(mapping))
                {
                    ShowDateEditPopup(grid, row, mapping);
                }
                else if (string.Equals(mapping, "Assignee", StringComparison.OrdinalIgnoreCase))
                {
                    ShowAssigneeEditPopup(grid, row);
                }
                else if (string.Equals(mapping, "ActionStatus", StringComparison.OrdinalIgnoreCase))
                {
                    ShowActionStatusEditPopup(grid, row);
                }
                else if (_userFieldPopupColumns.TryGetValue(mapping, out var category))
                {
                    ShowUserFieldEditPopup(grid, row, mapping, category);
                }
                else if (string.Equals(mapping, "LastComment", StringComparison.OrdinalIgnoreCase))
                {
                    // Route Comments click through CellTapped instead of per-cell PreviewMouseLeftButtonUp
                    // (used to be re-attached on every row recycle during scroll).
                    _ = OpenCommentsDialogForAsync(row);
                }
                else if (string.Equals(mapping, "MbawData", StringComparison.OrdinalIgnoreCase))
                {
                    OpenMbawPreviewFor(row);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CellTapped] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens a read-only preview dialog for the MbawData column. Mirrors the legacy
        /// MbawCell_Click handler but takes a row directly so it can be invoked from CellTapped
        /// without re-binding a per-row click handler on every row recycle during scroll.
        /// </summary>
        private void OpenMbawPreviewFor(ReconciliationViewData row)
        {
            if (row == null) return;
            try
            {
                var dlg = new PreviewTextDialog
                {
                    Owner = Window.GetWindow(this)
                };
                dlg.SetTitle("Mbaw Data");
                dlg.SetContent(row.MbawData ?? string.Empty);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenMbawPreviewFor] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Lazy-creates (or reuses) the shared popup shell and hosts <paramref name="content"/> in it.
        /// Pass <paramref name="dropdownOwner"/> when the content contains a ComboBox or DatePicker
        /// whose inner dropdown must be explicitly closed before the outer popup is dismissed.
        /// </summary>
        private void OpenEditPopup(FrameworkElement content, FrameworkElement dropdownOwner = null)
        {
            CloseEditPopup();

            if (_cellEditPopup == null)
            {
                _cellEditBorder = new Border
                {
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Background = System.Windows.Media.Brushes.White,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, Opacity = 0.3, ShadowDepth = 2 }
                };
                _cellEditPopup = new Popup
                {
                    Child = _cellEditBorder,
                    Placement = PlacementMode.MousePoint,
                    StaysOpen = false,
                    AllowsTransparency = true
                };
            }

            _cellEditBorder.Child = content;
            _cellEditInner = dropdownOwner ?? content;
            _cellEditPopup.IsOpen = true;
        }

        private void CloseEditPopup()
        {
            // Close the inner dropdown BEFORE detaching the editor from the visual tree,
            // otherwise the ComboBox/DatePicker's own top-level popup window stays visible
            // as an orphan (the reported "list reste ouverte" bug).
            if (_cellEditInner is ComboBox cb) cb.IsDropDownOpen = false;
            else if (_cellEditInner is DatePicker dp) dp.IsDropDownOpen = false;
            _cellEditInner = null;

            if (_cellEditPopup != null)
            {
                _cellEditPopup.IsOpen = false;
                // Release the content reference so it can be GC'd, but keep the Popup/Border
                // alive for reuse by the next OpenEditPopup call.
                if (_cellEditBorder != null) _cellEditBorder.Child = null;
            }
        }

        private void ShowDateEditPopup(SfDataGrid grid, ReconciliationViewData row, string propertyName)
        {
            var prop = typeof(ReconciliationViewData).GetProperty(propertyName);
            if (prop == null) return;
            var currentValue = prop.GetValue(row) as DateTime?;

            var dp = new DatePicker
            {
                Language = System.Windows.Markup.XmlLanguage.GetLanguage("fr-FR"),
                SelectedDateFormat = DatePickerFormat.Short,
                SelectedDate = currentValue,
                Width = 140,
                Margin = new Thickness(4)
            };
            var todayBtn = new Button { Content = "📅 Today", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2, 4, 4, 4), FontSize = 11 };
            var clearBtn = new Button { Content = "✕", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 4, 2, 4), FontSize = 11, ToolTip = "Clear date" };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = System.Windows.Media.Brushes.White };
            panel.Children.Add(dp);
            panel.Children.Add(todayBtn);
            panel.Children.Add(clearBtn);

            dp.SelectedDateChanged += async (s, args) =>
            {
                prop.SetValue(row, dp.SelectedDate);
                CloseEditPopup();
                await SaveEditedRowAsync(row);
            };
            todayBtn.Click += async (s, args) =>
            {
                prop.SetValue(row, DateTime.Today);
                CloseEditPopup();
                await SaveEditedRowAsync(row);
            };
            clearBtn.Click += async (s, args) =>
            {
                prop.SetValue(row, null);
                CloseEditPopup();
                await SaveEditedRowAsync(row);
            };

            OpenEditPopup(panel, dp);
        }

        private void ShowAssigneeEditPopup(SfDataGrid grid, ReconciliationViewData row)
        {
            var cb = new ComboBox
            {
                Width = 180,
                Margin = new Thickness(4),
                DisplayMemberPath = "Name",
                SelectedValuePath = "Id",
                SelectedValue = row.Assignee,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            // AssigneeOptions is a property on ReconciliationView (partial class in Options.cs)
            try { cb.ItemsSource = AssigneeOptions; } catch { }

            cb.SelectionChanged += async (s, args) =>
            {
                row.Assignee = cb.SelectedValue?.ToString();
                CloseEditPopup();
                await SaveEditedRowAsync(row);
            };
            // Safety net: if the user dismisses the dropdown (Escape, click outside, click on
            // another cell), close the outer popup too. Deferred so SelectionChanged's
            // CloseEditPopup runs first when a selection is made (idempotent otherwise).
            cb.DropDownClosed += (s, args) =>
                Dispatcher?.BeginInvoke(new Action(() => CloseEditPopup()), System.Windows.Threading.DispatcherPriority.Background);

            OpenEditPopup(cb);
            cb.IsDropDownOpen = true;
        }

        /// <summary>
        /// Shared popup editor for Action / KPI / IncidentType / ReasonNonRisky columns.
        /// Replaces the previous GridTemplateColumn+ComboBox EditTemplate — those templates
        /// were instantiated per row and dragged the scroll performance down. A single popup
        /// is shown on tap and disposed on selection.
        /// </summary>
        private void ShowUserFieldEditPopup(SfDataGrid grid, ReconciliationViewData row, string propertyName, string category)
        {
            var prop = typeof(ReconciliationViewData).GetProperty(propertyName);
            if (prop == null) return;

            // The converter returns a cached List<UserFieldOption> from its static cache (keyed by
            // category+accountSide). Feed it straight to ItemsSource — no per-open copy needed.
            System.Collections.IEnumerable options = Array.Empty<UserFieldOption>();
            try
            {
                options = _sharedUserFieldConverter.Convert(
                    new object[] { row.Account_ID, AllUserFields, CurrentCountryObject },
                    typeof(System.Collections.IEnumerable),
                    category,
                    System.Globalization.CultureInfo.CurrentCulture) as System.Collections.IEnumerable
                    ?? Array.Empty<UserFieldOption>();
            }
            catch { }

            var cb = new ComboBox
            {
                Width = 180,
                Margin = new Thickness(4),
                DisplayMemberPath = "USR_FieldName",
                SelectedValuePath = "USR_ID",
                SelectedValue = prop.GetValue(row),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ItemsSource = options
            };

            cb.SelectionChanged += async (s, args) =>
            {
                try
                {
                    // SelectedValue is int? for Action/KPI/IncidentType/ReasonNonRisky
                    int? newId = cb.SelectedValue as int?;
                    prop.SetValue(row, newId);

                    // Keep the display-name cache in sync so the cell repaints immediately
                    // without waiting for a full enrich pass.
                    var displayName = (cb.SelectedItem as UserFieldOption)?.USR_FieldName ?? string.Empty;
                    switch (propertyName)
                    {
                        case "Action":         row.ActionDisplayName = displayName; break;
                        case "KPI":            row.KpiDisplayName = displayName; break;
                        case "IncidentType":   row.IncidentTypeDisplayName = displayName; break;
                        case "ReasonNonRisky": row.ReasonNonRiskyDisplayName = displayName; break;
                    }

                    CloseEditPopup();
                    await SaveEditedRowAsync(row);

                    // If the Action is TRIGGER, propagate to the whole group (same BGI / BGPMT / InternalInvoiceRef).
                    // This replicates the legacy UserFieldComboBox_SelectionChanged behaviour.
                    if (string.Equals(propertyName, "Action", StringComparison.OrdinalIgnoreCase)
                        && newId.HasValue && newId.Value == (int)ActionType.Trigger)
                    {
                        await PropagateTriggerToGroupAsync(row);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UserFieldPopup] {propertyName} save error: {ex.Message}");
                }
            };
            // Safety net: dropdown dismissed without selection (Escape / click outside) → close outer popup.
            cb.DropDownClosed += (s, args) =>
                Dispatcher?.BeginInvoke(new Action(() => CloseEditPopup()), System.Windows.Threading.DispatcherPriority.Background);

            OpenEditPopup(cb);
            cb.IsDropDownOpen = true;
        }

        /// <summary>
        /// Simple two-state popup for ActionStatus (PENDING/DONE). Cheaper than a ComboBox
        /// with custom items: just two buttons in a stack.
        /// </summary>
        private void ShowActionStatusEditPopup(SfDataGrid grid, ReconciliationViewData row)
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(2) };
            var btnPending = new Button { Content = "⏳ PENDING", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Left };
            var btnDone = new Button { Content = "✓ DONE", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Left };
            stack.Children.Add(btnPending);
            stack.Children.Add(btnDone);

            async void Commit(bool? done)
            {
                try
                {
                    row.ActionStatus = done;
                    CloseEditPopup();
                    await SaveEditedRowAsync(row);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ActionStatusPopup] error: {ex.Message}");
                }
            }
            btnPending.Click += (s, a) => Commit(false);
            btnDone.Click += (s, a) => Commit(true);

            OpenEditPopup(stack);
        }
    }
}
