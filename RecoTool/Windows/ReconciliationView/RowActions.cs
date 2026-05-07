using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RecoTool.API;
using RecoTool.Services;
using RecoTool.Services.DTOs;
using RecoTool.Models;
using RecoTool.UI.Helpers;
using System.Threading.Tasks;

namespace RecoTool.Windows
{
    public partial class ReconciliationView
    {
        private async void RunRulesNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_reconciliationService == null)
                {
                    ShowError("Service not available.");
                    return;
                }

                // Use selection if any, otherwise all rows in current filtered set
                var selected = GetCurrentSelection();
                var rows = (selected != null && selected.Count > 0)
                    ? selected
                    : (_filteredData?.ToList() ?? ViewData?.ToList() ?? new List<ReconciliationViewData>());

                // Always exclude archived rows
                rows = rows?.Where(r => r != null && !r.IsDeleted).ToList();

                if (rows == null || rows.Count == 0)
                {
                    UpdateStatusInfo("No active rows to apply rules.");
                    return;
                }

                var ids = rows.Select(r => r?.ID)
                              .Where(id => !string.IsNullOrWhiteSpace(id))
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();
                if (ids.Count == 0)
                {
                    UpdateStatusInfo("No valid IDs to apply rules.");
                    return;
                }

                UpdateStatusInfo($"Applying rules to {ids.Count} row(s)...");
                var count = await _reconciliationService.ApplyRulesNowAsync(ids).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateStatusInfo($"Rules applied to {count} row(s). Reloading...");
                    Refresh();
                    DataChanged?.Invoke();
                });
            }
            catch (Exception ex)
            {
                ShowError($"Failed to run rules: {ex.Message}");
            }
        }

        private IEnumerable<UserField> GetUserFieldOptionsForRow(string category, ReconciliationViewData row)
        {
            try
            {
                var all = AllUserFields;
                var country = CurrentCountryObject;
                if (row == null || all == null || country == null) return Enumerable.Empty<UserField>();

                bool isPivot = string.Equals(row.Account_ID?.Trim(), country.CNT_AmbrePivot?.Trim(), StringComparison.OrdinalIgnoreCase);
                bool isReceivable = string.Equals(row.Account_ID?.Trim(), country.CNT_AmbreReceivable?.Trim(), StringComparison.OrdinalIgnoreCase);

                bool incident = string.Equals(category, "Incident Type", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(category, "INC", StringComparison.OrdinalIgnoreCase);

                bool risky = string.Equals(category, "Risky", StringComparison.OrdinalIgnoreCase);

                IEnumerable<UserField> query = incident
                    ? all.Where(u => string.Equals(u.USR_Category, "Incident Type", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(u.USR_Category, "INC", StringComparison.OrdinalIgnoreCase))
                    : risky
                        ? all.Where(u => string.Equals(u.USR_Category, "Risky", StringComparison.OrdinalIgnoreCase))
                        : all.Where(u => string.Equals(u.USR_Category, category, StringComparison.OrdinalIgnoreCase));

                if (isPivot)
                    query = query.Where(u => u.USR_Pivot);
                else if (isReceivable)
                    query = query.Where(u => u.USR_Receivable);
                else
                    return Enumerable.Empty<UserField>();

                return query.OrderBy(u => u.USR_FieldName).ToList();
            }
            catch { return Enumerable.Empty<UserField>(); }
        }

        // Open full conversation and allow appending a new comment line.
        // PERF: Called directly by CellTapped (Editing.cs) — the old PreviewMouseLeftButtonUp
        // on the cell template was re-attached on every row recycle during scroll.
        internal async Task OpenCommentsDialogForAsync(ReconciliationViewData row)
        {
            try
            {
                if (row == null || _reconciliationService == null) return;

                var dlg = new CommentsDialog
                {
                    Owner = Window.GetWindow(this)
                };
                dlg.SetUsers(AssigneeOptions);
                dlg.SetConversationText(ResolveCommentsForDisplay(row.Comments ?? string.Empty));
                var res = dlg.ShowDialog();
                if (res == true)
                {
                    var user = ResolveUserDisplayName(_reconciliationService.CurrentUser);
                    var newLine = dlg.GetNewCommentText()?.Trim();
                    if (!string.IsNullOrWhiteSpace(newLine))
                    {
                        string prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {user}: ";
                        string existing = row.Comments?.TrimEnd();
                        string appended = string.IsNullOrWhiteSpace(existing)
                            ? prefix + newLine
                            : existing + Environment.NewLine + prefix + newLine;

                        // Update view model immediately
                        row.Comments = appended;

                        // Persist to reconciliation
                        var reco = await _reconciliationService.GetOrCreateReconciliationAsync(row.ID);
                        reco.Comments = appended;
                        await _reconciliationService.SaveReconciliationAsync(reco);
                        StampRowsModified(new[] { row });
                        
                        // Refresh KPIs to reflect changes immediately
                        UpdateKpis(_filteredData);

                        // Refresh @mention badge (comment may contain new mentions)
                        try { RefreshMentionBadge(); } catch { }

                        // Best-effort background sync
                        try { ScheduleBulkPushDebounced(); } catch { }
                    }
                }
            } catch (Exception ex) 
            {
                ShowError($"Failed to update comments: {ex.Message}");
            }
        }
        /// <summary>
        /// Gestion du clic sur les items du menu contextuel : Action, KPI,
        /// Incident Type **et** Reason Non‑Risky (liste déroulante).  
        /// Fonctionne en mode *single* ou *bulk* (plusieurs lignes sélectionnées).
        /// </summary>
        private async void QuickSetUserFieldMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                /* -----------------------------------------------------------------
                 * Récupération du MenuItem et de la ligne concernée
                 * -----------------------------------------------------------------*/
                var mi = sender as MenuItem;
                if (mi == null) return;

                // La ligne peut être dans le DataContext du MenuItem ou, à défaut,
                // dans le PlacementTarget du ContextMenu.
                var row = mi.DataContext as ReconciliationViewData;
                if (row == null)
                {
                    var cm = VisualTreeHelpers.FindParent<ContextMenu>(mi);
                    var fe = cm?.PlacementTarget as FrameworkElement;
                    row = fe?.DataContext as ReconciliationViewData;
                }
                if (row == null) return;

                /* -----------------------------------------------------------------
                 * 3️⃣  Détection de la catégorie (Action / KPI / Incident / ReasonNonRisky)
                 * -----------------------------------------------------------------*/
                var category = mi.Tag as string ?? string.Empty;
                bool isAction = string.Equals(category, "Action", StringComparison.OrdinalIgnoreCase);
        
                bool isKpi = string.Equals(category, "KPI", StringComparison.OrdinalIgnoreCase);
                bool isInc = string.Equals(category, "Incident Type", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(category, "IncidentType", StringComparison.OrdinalIgnoreCase);
                // ← c’est la **liste déroulante** qui doit mettre à jour ReasonNonRisky
                bool isReasonNonRisky = string.Equals(category, "Risky", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(category, "RISKY", StringComparison.OrdinalIgnoreCase);

                if (!isAction && !isKpi && !isInc && !isReasonNonRisky)
                    return;                 // rien à faire

                /* -----------------------------------------------------------------
                 * 4️⃣  Valeur à appliquer (int? pour les champs référentiels,
                 *     y compris ReasonNonRisky)
                 * -----------------------------------------------------------------*/
                int? newId = null;          // Action / KPI / Incident / ReasonNonRisky

                if (mi.CommandParameter != null)
                {
                    // Tous les menus passent un int (ou null) – même ReasonNonRisky
                    if (mi.CommandParameter is int id) newId = id;
                    else if (int.TryParse(mi.CommandParameter.ToString(),
                                          out var parsed)) newId = parsed;
                }

                /* -----------------------------------------------------------------
                 * 5️⃣  Détermination des lignes cibles (single / bulk)
                 * -----------------------------------------------------------------*/
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                var targetRows = new List<ReconciliationViewData>();

                if (sfGrid?.SelectedItems != null && sfGrid.SelectedItems.Count > 1)
                    targetRows.AddRange(sfGrid.SelectedItems.OfType<ReconciliationViewData>());
                else
                    targetRows.Add(row);               // seule la ligne sur laquelle on a cliqué

                /* -----------------------------------------------------------------
                 * 6️⃣  Confirmation lorsqu’on **vide** le champ
                 * -----------------------------------------------------------------*/
                if ((isAction && newId == null) ||
                    (isKpi && newId == null) ||
                    (isInc && newId == null) ||
                    (isReasonNonRisky && newId == null))
                {
                    bool anyHasValue = targetRows.Any(r =>
                        (isAction && r.Action.HasValue) ||
                        (isKpi && r.KPI.HasValue) ||
                        (isInc && r.IncidentType.HasValue) ||
                        (isReasonNonRisky && r.ReasonNonRisky.HasValue));

                    if (anyHasValue)
                    {
                        string label = isAction ? "Action"
                                      : isKpi ? "KPI"
                                      : isInc ? "Incident Type"
                                      : "Reason Non‑Risky";

                        if (MessageBox.Show(
                                $"Clear {label} for {targetRows.Count} selected row(s) ?",
                                "Confirm",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question) != MessageBoxResult.Yes)
                            return;
                    }
                }

                /* -----------------------------------------------------------------
                 * 7️⃣  Preview / application des règles automatiques (bulk)
                 * -----------------------------------------------------------------*/
                bool applyRules = false;

                if (targetRows.Count > 1)                     // bulk
                {
                    int rowsWithRules = 0;
                    foreach (var r in targetRows.Take(500))    // on ne parcourt que les 500 premiers pour la perf
                    {
                        try
                        {
                            var preview = await _reconciliationService.PreviewRulesForEditAsync(r.ID);
                            if (preview?.Rule != null) rowsWithRules++;
                        }
                        catch { /* ignore */ }
                    }

                    if (rowsWithRules > 0)
                    {
                        string fieldName = isReasonNonRisky ? "Reason Non‑Risky"
                                         : isAction ? "Action"
                                         : isKpi ? "KPI"
                                         : "Incident Type";

                        // fieldValue – texte affiché dans la boîte de dialogue de preview (bulk)
                        string fieldValue = newId.HasValue
                            ? $"to '{GetUserFieldName(category, newId.Value)}'"
                            : "(cleared)";

                        var answer = MessageBox.Show(
        $@"BULK UPDATE: {fieldName} {fieldValue} for {targetRows.Count} rows

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

⚠️ AUTOMATIC RULES DETECTION

At least {rowsWithRules} row(s) will trigger automatic rules that could:
  • Change other fields (KPI, Incident Type, etc.)
  • Add comments
  • Set reminders

Do you want to apply these automatic rules?

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ : Apply rules automatically (recommended)
• NO  : Only update the field above
• CANCEL : Abort the operation",
                            $"Apply Automatic Rules? ({rowsWithRules} rules)",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Question);

                        if (answer == MessageBoxResult.Cancel) return;
                        applyRules = (answer == MessageBoxResult.Yes);
                    }
                }
                else
                {
                    // ligne unique → on applique toujours les règles (comportement historique)
                    applyRules = true;
                }

                /* -----------------------------------------------------------------
                 * 8️⃣  Construction des mises à jour
                 * -----------------------------------------------------------------*/
                var updates = new List<Reconciliation>();
                int rulesAppliedCnt = 0;

                foreach (var r in targetRows)
                {
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);

                    // ---- 8.1  Mise à jour du champ demandé ----
                    string stampField = null;
                    if (isAction)
                    {
                        UserFieldUpdateService.ApplyAction(r, reco, newId, AllUserFields);
                        ViewDataEnricher.RefreshUserFieldDisplay(r, "Action");
                        stampField = "Action";
                    }
                    else if (isKpi)
                    {
                        UserFieldUpdateService.ApplyKpi(r, reco, newId);
                        ViewDataEnricher.RefreshUserFieldDisplay(r, "KPI");
                        stampField = "KPI";
                    }
                    else if (isInc)
                    {
                        UserFieldUpdateService.ApplyIncidentType(r, reco, newId);
                        ViewDataEnricher.RefreshUserFieldDisplay(r, "Incident Type");
                        stampField = "IncidentType";
                    }
                    else if (isReasonNonRisky)                      // ---- Reason Non‑Risky ----
                    {
                        r.ReasonNonRisky = newId;
                        reco.ReasonNonRisky = newId;
                        ViewDataEnricher.RefreshUserFieldDisplay(r, "ReasonNonRisky");
                        // Si vous avez un helper dédié pour rafraîchir l’icône, appelez‑le ici.
                        stampField = "ReasonNonRisky";
                    }

                    // Stamp user-edit protection on the bulk-set field so rules won't silently overwrite.
                    if (stampField != null)
                        RecoTool.Services.Rules.RuleApplicationHelper.StampUserEdit(reco, stampField);

                    // ---- 8.2  Application des règles automatiques (si demandé) ----
                    if (applyRules)
                    {
                        try
                        {
                            var preview = await _reconciliationService.PreviewRulesForEditAsync(r.ID);
                            if (preview?.Rule != null)
                            {
                                // Action
                                if (preview.NewActionIdSelf.HasValue)
                                    UserFieldUpdateService.ApplyAction(r, reco,
                                        preview.NewActionIdSelf.Value, AllUserFields);

                                // KPI
                                if (preview.NewKpiIdSelf.HasValue)
                                {
                                    r.KPI = preview.NewKpiIdSelf.Value;
                                    reco.KPI = preview.NewKpiIdSelf.Value;
                                }

                                // Incident
                                if (preview.NewIncidentTypeIdSelf.HasValue)
                                {
                                    r.IncidentType = preview.NewIncidentTypeIdSelf.Value;
                                    reco.IncidentType = preview.NewIncidentTypeIdSelf.Value;
                                }

                                // RiskyItem (inchangé ici – la règle peut le toucher)
                                if (preview.NewRiskyItemSelf.HasValue)
                                {
                                    r.RiskyItem = preview.NewRiskyItemSelf.Value;
                                    reco.RiskyItem = preview.NewRiskyItemSelf.Value;
                                }

                                // **ReasonNonRisky** (c’est la partie qui nous intéresse)
                                if (preview.NewReasonNonRiskyIdSelf.HasValue)
                                {
                                    r.ReasonNonRisky = preview.NewReasonNonRiskyIdSelf.Value;
                                    reco.ReasonNonRisky = preview.NewReasonNonRiskyIdSelf.Value;
                                }

                                // … (autres champs automatiques, commentaires, etc.) …

                                // Commentaire généré par la règle
                                if (!string.IsNullOrWhiteSpace(preview.UserMessage))
                                {
                                    var curUser = ResolveUserDisplayName(_reconciliationService?.CurrentUser ?? Environment.UserName);
                                    var prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {curUser}: ";
                                    var msg = prefix + $"[Rule {preview.Rule.RuleId ?? "(unnamed)"}] {preview.UserMessage}";

                                    if (string.IsNullOrWhiteSpace(r.Comments))
                                    {
                                        r.Comments = msg;
                                        reco.Comments = msg;
                                    }
                                    else if (!r.Comments.Contains(msg))
                                    {
                                        r.Comments = msg + Environment.NewLine + r.Comments;
                                        reco.Comments = msg + Environment.NewLine + reco.Comments;
                                    }
                                    rulesAppliedCnt++;
                                }
                            }
                        }
                        catch { /* on continue même si la règle échoue sur une ligne */ }
                    }

                    updates.Add(reco);
                }

                /* -----------------------------------------------------------------
                 * 9️⃣  Persistance batch
                 * -----------------------------------------------------------------*/
                await _reconciliationService.SaveReconciliationsAsync(updates,
                                                                     applyRulesOnEdit: false);

                /* -----------------------------------------------------------------
                 * 10️⃣  Propagation d’un éventuel « Trigger » (spécifique Action)
                 * -----------------------------------------------------------------*/
                if (isAction && newId.HasValue && newId.Value == (int)ActionType.Trigger)
                {
                    foreach (var r in targetRows)
                        await PropagateTriggerToGroupAsync(r);
                }

                /* -----------------------------------------------------------------
                 * 11️⃣  Feedback UI
                 * -----------------------------------------------------------------*/
                if (applyRules && rulesAppliedCnt > 0)
                    ShowToast($"✨ Rules applied to {rulesAppliedCnt} of {targetRows.Count} rows");

                // Rafraîchissement des KPI (et du ReasonNonRisky si vous avez un helper dédié)
                UpdateKpis(_filteredData);
                // RefreshReasonNonRiskyColumn(_filteredData);   // ← à implémenter si besoin

                // Synchronisation en arrière‑plan (dé‑bouncèe)
                try { ScheduleBulkPushDebounced(); } catch { }

                // Stamp rows + refresh activity log
                StampRowsModified(targetRows);
                try { RefreshActivityLog(); } catch { }

                // -----------------------------------------------------------------
                //  Construction du texte qui sera affiché dans le toast final
                // -----------------------------------------------------------------
                string summary;

                if (isReasonNonRisky)                     // ---------- ReasonNonRisky ----------
                {
                    if (newId.HasValue)
                        summary = $"Reason Non‑Risky = {GetUserFieldName("ReasonNonRisky", newId.Value)}";
                    else
                        summary = "Reason Non‑Risky cleared";
                }
                else if (isAction)                         // ---------- Action ----------
                {
                    if (newId.HasValue)
                        summary = $"Action = {GetUserFieldName("Action", newId.Value)}";
                    else
                        summary = "Action cleared";
                }
                else if (isKpi)                            // ---------- KPI ----------
                {
                    if (newId.HasValue)
                        summary = $"KPI = {GetUserFieldName("KPI", newId.Value)}";
                    else
                        summary = "KPI cleared";
                }
                else if (isInc)                            // ---------- Incident ----------
                {
                    if (newId.HasValue)
                        summary = $"Incident Type = {GetUserFieldName("Incident Type", newId.Value)}";
                    else
                        summary = "Incident Type cleared";
                }
                else
                {
                    summary = string.Empty;
                }

                if (!string.IsNullOrEmpty(summary))
                    ShowToast($"{summary} on {targetRows.Count} line(s)");

                // Notify parent page to refresh sibling views
                DataChanged?.Invoke();
            }
            catch (Exception ex)
            {
                ShowError($"Save error: {ex.Message}");
            }
        }


        // Set comment on selected rows (append as conversation line)
        private async void QuickSetCommentMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_reconciliationService == null) return;

                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                var selected = sfGrid?.SelectedItems?.OfType<ReconciliationViewData>().ToList() ?? new List<ReconciliationViewData>();
                if (selected.Count == 0) return;

                var text = ShowTextInputDialog($"Set Comment for {selected.Count} row(s)", multiLine: true);
                if (text == null) return;

                var user = ResolveUserDisplayName(_reconciliationService.CurrentUser ?? Environment.UserName);
                string prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {user}: ";
                var updates = new List<Reconciliation>();
                foreach (var r in selected)
                {
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);
                    string existing = r.Comments?.TrimEnd();
                    string appended = string.IsNullOrWhiteSpace(existing)
                        ? prefix + text
                        : existing + Environment.NewLine + prefix + text;
                    r.Comments = appended;
                    reco.Comments = appended;
                    updates.Add(reco);
                }
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);
                StampRowsModified(selected);
                AfterSave();
                try { RefreshMentionBadge(); } catch { }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to set bulk comment: {ex.Message}");
            }
        }

        // Quick mark action as done
        private async void QuickMarkActionDoneMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SetActionStatusAsync(sender, true, "DONE");
        }

        // Quick mark action as pending
        private async void QuickMarkActionPendingMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SetActionStatusAsync(sender, false, "PENDING");
        }

        private async Task SetActionStatusAsync(object sender, bool isDone, string statusLabel)
        {
            try
            {
                if (_reconciliationService == null) return;
                var targetRows = GetTargetRows(sender);
                if (targetRows.Count == 0) return;

                var triggerActionId = (int)ActionType.Trigger;
                var now = DateTime.UtcNow;
                var updates = new List<Reconciliation>();
                foreach (var r in targetRows)
                {
                    if (!r.Action.HasValue) continue;
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);

                    // When the user marks a TRIGGER row as DONE via the menu, apply the
                    // full TRIGGER DONE payload (KPI + ReasonNonRisky + RiskyItem) so the
                    // outcome matches what the right-click DWINGS flow produces. Mirror to
                    // the view-row so the grid reflects the change immediately.
                    if (isDone && r.Action.Value == triggerActionId)
                    {
                        ApplyTriggerDonePayload(reco, now);
                        r.Action = reco.Action;
                        r.ActionStatus = reco.ActionStatus;
                        r.ActionDate = reco.ActionDate;
                        r.KPI = reco.KPI;
                        r.ReasonNonRisky = reco.ReasonNonRisky;
                        // ReconciliationViewData.RiskyItem is non-nullable bool; reco.RiskyItem is bool?.
                        r.RiskyItem = reco.RiskyItem.GetValueOrDefault(false);
                    }
                    else
                    {
                        r.ActionStatus = isDone;
                        r.ActionDate = DateTime.Now;
                        reco.ActionStatus = isDone;
                        reco.ActionDate = r.ActionDate;
                        // Stamp user-edit protection on ActionStatus
                        RecoTool.Services.Rules.RuleApplicationHelper.StampUserEdit(reco, "ActionStatus");
                    }
                    updates.Add(reco);
                }
                if (updates.Count == 0) return;
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);
                StampRowsModified(targetRows);
                AfterSave();
            }
            catch (Exception ex)
            {
                ShowError($"Failed to update status: {ex.Message}");
            }
        }

        private async void QuickTakeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return;
                var itemsSource = sfGrid.ItemsSource as System.Collections.IEnumerable;
                var targetRows = itemsSource?.OfType<ReconciliationViewData>().ToList() ?? new List<ReconciliationViewData>();
                if (targetRows.Count == 0) return;

                var user = _reconciliationService.CurrentUser;
                var updates = new List<Reconciliation>();
                foreach (var r in targetRows)
                {
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);
                    r.Assignee = user;
                    reco.Assignee = user;
                    // Stamp user-edit protection on Assignee
                    RecoTool.Services.Rules.RuleApplicationHelper.StampUserEdit(reco, "Assignee");
                    updates.Add(reco);
                }
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);
                StampRowsModified(targetRows);
                AfterSave();
            }
            catch (Exception ex)
            {
                ShowError($"Failed to assign: {ex.Message}");
            }
        }

        // Quick set reminder
        private async void QuickSetReminderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var targetRows = GetTargetRows(sender);
                if (targetRows.Count == 0) return;

                DateTime? selDate = ShowDatePickerDialog();
                if (!selDate.HasValue) return;

                var currentUser = _reconciliationService.CurrentUser;
                var updates = new List<Reconciliation>();
                foreach (var r in targetRows)
                {
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);
                    r.ToRemindDate = selDate.Value;
                    r.ToRemind = true;
                    if (string.IsNullOrWhiteSpace(r.Assignee))
                    {
                        r.Assignee = currentUser;
                        reco.Assignee = currentUser;
                    }
                    reco.ToRemindDate = selDate.Value;
                    reco.ToRemind = true;
                    // Stamp user-edit protection on reminder fields
                    RecoTool.Services.Rules.RuleApplicationHelper.StampUserEdit(reco, "ToRemind", "ToRemindDate");
                    updates.Add(reco);
                }
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);
                StampRowsModified(targetRows);
                AfterSave();
            }
            catch (Exception ex)
            {
                ShowError($"Failed to set reminder: {ex.Message}");
            }
        }
        // Quick set First Claim Date = user‑chosen date (or cancel)
        private async void QuickSetFirstClaimTodayMenuItem_Click(object sender, RoutedEventArgs e)
            => await SetClaimDateAsync(sender, isLastClaim: false);
        private async void QuickSetLastClaimTodayMenuItem_Click(object sender, RoutedEventArgs e)
            => await SetClaimDateAsync(sender, isLastClaim: true);

        /// <summary>
        /// Affiche une petite fenêtre modal contenant un <see cref="DatePicker"/>
        /// et retourne la date sélectionnée ou <c>null</c> si l'utilisateur annule.
        /// </summary>
        private DateTime? ShowDatePickerDialog()
        {
            // Crée la fenêtre
            var dlg = new Window
            {
                Title = "Sélectionner une date de première réclamation",
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Application.Current?.MainWindow,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false
            };

            // Crée le DatePicker et les boutons OK / Annuler
            var datePicker = new DatePicker
            {
                SelectedDate = DateTime.Today,   // valeur par défaut
                Margin = new Thickness(10)
            };

            var okButton = new Button
            {
                Content = "OK",
                Width = 75,
                IsDefault = true,
                Margin = new Thickness(5)
            };
            okButton.Click += (_, __) => dlg.DialogResult = true;

            var cancelButton = new Button
            {
                Content = "Annuler",
                Width = 75,
                IsCancel = true,
                Margin = new Thickness(5)
            };
            cancelButton.Click += (_, __) => dlg.DialogResult = false;

            // Layout
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { okButton, cancelButton },
                Margin = new Thickness(0, 0, 10, 10)
            };

            var mainPanel = new StackPanel
            {
                Children = { datePicker, buttonPanel }
            };
            dlg.Content = mainPanel;

            // Affichage modal
            bool? result = dlg.ShowDialog();

            // Retourner la date uniquement si l'utilisateur a cliqué sur OK
            return (result == true && datePicker.SelectedDate.HasValue)
                ? datePicker.SelectedDate.Value
                : (DateTime?)null;
        }

        #region Shared Helpers

        /// <summary>
        /// Resolves target rows from a context menu click: uses multi-selection if the clicked
        /// row is part of the selection, otherwise returns only the clicked row.
        /// </summary>
        private List<ReconciliationViewData> GetTargetRows(object sender)
        {
            var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
            var rowCtx = (sender as FrameworkElement)?.DataContext as ReconciliationViewData;
            if (sfGrid?.SelectedItems?.Count > 1 && rowCtx != null
                && sfGrid.SelectedItems.OfType<ReconciliationViewData>().Contains(rowCtx))
                return sfGrid.SelectedItems.OfType<ReconciliationViewData>().ToList();
            return rowCtx != null ? new List<ReconciliationViewData> { rowCtx } : new List<ReconciliationViewData>();
        }

        /// <summary>
        /// Common post-save refresh: update KPIs, schedule sync push, optionally raise DataChanged.
        /// </summary>
        private void AfterSave(bool raiseDataChanged = false)
        {
            UpdateKpis(_filteredData);
            try { ScheduleBulkPushDebounced(); } catch { }
            try { RefreshActivityLog(); } catch { }
            if (raiseDataChanged) DataChanged?.Invoke();
        }

        /// <summary>
        /// Shows a modal text input dialog. Returns the entered text, or null if cancelled.
        /// </summary>
        private string ShowTextInputDialog(string title, bool multiLine = false)
        {
            var prompt = new Window
            {
                Title = title,
                Width = multiLine ? 480 : 400,
                Height = multiLine ? 220 : 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize
            };
            var grid = new Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new RowDefinition { Height = multiLine ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tb = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            if (multiLine) { tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            Grid.SetRow(tb, 0);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnOk = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var btnCancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            btnOk.Click += (_, __) => { prompt.DialogResult = true; prompt.Close(); };
            btnCancel.Click += (_, __) => { prompt.DialogResult = false; prompt.Close(); };
            panel.Children.Add(btnOk);
            panel.Children.Add(btnCancel);
            Grid.SetRow(panel, 1);
            grid.Children.Add(tb);
            grid.Children.Add(panel);
            prompt.Content = grid;
            return prompt.ShowDialog() == true ? tb.Text : null;
        }

        /// <summary>
        /// Marks a set of reconciliation rows as TRIGGER DONE.
        /// Sets Action=Trigger, ActionStatus=true, ActionDate=TriggerDate=now,
        /// and the post-trigger business state KPI=PaidButNotReconciled,
        /// ReasonNonRisky=CollectedCommissionsCredit67P, RiskyItem=false (commissions
        /// have already been collected on the 67P account, so the line is no longer risky).
        /// User-edit stamps are applied so the rules engine respects this state.
        /// Skips IDs already present in <paramref name="alreadyProcessedIds"/>.
        /// </summary>
        private async Task<List<Reconciliation>> MarkTriggerDoneAsync(
            IEnumerable<string> ids, HashSet<string> alreadyProcessedIds = null)
        {
            var now = DateTime.UtcNow;
            var updates = new List<Reconciliation>();
            foreach (var id in ids)
            {
                var trimmed = id?.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (alreadyProcessedIds != null && alreadyProcessedIds.Contains(trimmed)) continue;
                var reco = await _reconciliationService.GetReconciliationByIdAsync(_currentCountryId, trimmed);
                if (reco != null)
                {
                    ApplyTriggerDonePayload(reco, now);
                    updates.Add(reco);
                }
            }
            return updates;
        }

        /// <summary>
        /// Applies the canonical TRIGGER DONE payload to an existing Reconciliation. Used by
        /// the right-click flow, the DWINGS Blue Button bulk window, and the menu-based
        /// "Mark Action Done" handler so all three paths produce the same final state.
        /// </summary>
        internal static void ApplyTriggerDonePayload(Reconciliation reco, DateTime now)
        {
            if (reco == null) return;
            reco.Action = (int)ActionType.Trigger;
            reco.ActionStatus = true;
            reco.ActionDate = now;
            reco.TriggerDate = now;
            // Post-trigger business state: commissions already collected on 67P.
            reco.KPI = (int)KPIType.PaidButNotReconciled;            // 18
            reco.ReasonNonRisky = (int)Risky.CollectedCommissionsCredit67P; // 30
            reco.RiskyItem = false;
            RecoTool.Services.Rules.RuleApplicationHelper.StampUserEdit(
                reco, "Action", "ActionStatus", "ActionDate", "TriggerDate", "KPI", "ReasonNonRisky", "RiskyItem");
        }

        /// <summary>
        /// Sets a claim date field on target rows. Used by both First Claim and Last Claim handlers.
        /// </summary>
        private async Task SetClaimDateAsync(object sender, bool isLastClaim)
        {
            try
            {
                var targetRows = GetTargetRows(sender);
                if (targetRows.Count == 0) return;

                DateTime? chosenDate = ShowDatePickerDialog();
                if (!chosenDate.HasValue) return;

                var updates = new List<Reconciliation>();
                foreach (var r in targetRows)
                {
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);
                    if (isLastClaim)
                    {
                        if (!r.FirstClaimDate.HasValue)
                        {
                            r.FirstClaimDate = chosenDate.Value;
                            reco.FirstClaimDate = chosenDate.Value;
                        }
                        r.LastClaimDate = chosenDate.Value;
                        reco.LastClaimDate = chosenDate.Value;
                        RecoTool.Services.Rules.RuleApplicationHelper.StampUserEdit(reco, "LastClaimDate", "FirstClaimDate");
                    }
                    else
                    {
                        r.FirstClaimDate = chosenDate.Value;
                        reco.FirstClaimDate = chosenDate.Value;
                        RecoTool.Services.Rules.RuleApplicationHelper.StampUserEdit(reco, "FirstClaimDate");
                    }
                    updates.Add(reco);
                }
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);
                StampRowsModified(targetRows);
                AfterSave();
            }
            catch (Exception ex)
            {
                ShowError($"Failed to set {(isLastClaim ? "Last" : "First")} Claim Date: {ex.Message}");
            }
        }

        #endregion

        // Helper: Get user field name by category and ID
        private string GetUserFieldName(string category, int id)
        {
            try
            {
                var list = AllUserFields;
                if (list == null) return id.ToString();
                var q = list.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(category))
                    q = q.Where(u => string.Equals(u?.USR_Category ?? string.Empty, category, StringComparison.OrdinalIgnoreCase));
                var uf = q.FirstOrDefault(u => u?.USR_ID == id) ?? list.FirstOrDefault(u => u?.USR_ID == id);
                return uf?.USR_FieldName ?? id.ToString();
            }
            catch { return id.ToString(); }
        }

        /// <summary>
        /// Fired after a bulk action (rules, quick-set, etc.) modifies data in this view.
        /// ReconciliationPage subscribes to refresh sibling views.
        /// </summary>
        public event Action DataChanged;

        /// <summary>
        /// Event for requesting to add rows to the linking basket (handled by ReconciliationPage).
        /// </summary>
        public event Action<List<ReconciliationViewData>> AddToLinkingBasketRequested;

        /// <summary>
        /// Current linking basket count — updated by ReconciliationPage after each basket change.
        /// Used to display the count in the context menu header.
        /// </summary>
        public int LinkingBasketCount { get; set; } = 0;

        private void SpiritGeneSearchMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rowData = (sender as FrameworkElement)?.DataContext as ReconciliationViewData;
                if (rowData == null) return;

                // Resolve SpiritGene service (try DI first, then create directly)
                var spiritGene = RecoTool.App.ServiceProvider?.GetService(typeof(RecoTool.API.SpiritGene)) as RecoTool.API.SpiritGene;
                if (spiritGene == null)
                {
                    try { spiritGene = new RecoTool.API.SpiritGene(); }
                    catch { }
                }
                if (spiritGene == null)
                {
                    ShowError("SpiritGene service is not available.");
                    return;
                }

                // Pre-fill search parameters from the selected row
                var window = new SpiritGeneSearchWindow(
                    spiritGene,
                    _offlineFirstService,
                    operationDate: rowData.Operation_Date,
                    amount: rowData.SignedAmount,
                    bic: null // BIC not directly available on the row
                );
                window.Owner = Window.GetWindow(this);
                window.Show(); // Non-modal so user can compare with the grid
            }
            catch (Exception ex)
            {
                ShowError($"Failed to open SpiritGene search: {ex.Message}");
            }
        }

        /// <summary>
        /// Single-row (or selection) DWINGS Blue Button processing.
        /// Mirrors the logic of DwingsButtonsWindow.BulkButton_Click but for the
        /// row(s) selected in the ReconciliationView context menu.
        /// </summary>
        private async void SingleProcessDwings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var country = CurrentCountryObject;
                if (country == null) { ShowError("No country loaded."); return; }
                var receivableId = country.CNT_AmbreReceivable;

                // ---- target rows (single or multi-select) ----
                var targetRows = GetTargetRows(sender);
                if (targetRows.Count == 0) return;

                // ---- build trigger items PER BGPMT (DWINGS API = one call per BGPMT) ----
                // We need the full dataset to find matching pivots
                var allData = _allViewData ?? _filteredData ?? ViewData?.ToList() ?? new List<ReconciliationViewData>();
                var triggerItems = new List<(string AllIds, string PaymentRef, DateTime? ValueDate,
                                             string RequestedAmount, string Currency, string Bgpmt,
                                             bool IsGrouped, ReconciliationViewData SourceRow)>();

                // Track all linked group IDs (by InternalInvoiceReference + BGPMT) for TRIGGER DONE expansion
                var allGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var processedBgpmts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Phase 2 (Partially-paid BGI): groups skipped because at least one row carries
                // a non-zero RemainingAmount. Surfaced in a single toast at the end so the
                // operator knows exactly why a Trigger they expected did not fire.
                var partiallyPaidSkipped = new List<string>();

                foreach (var row in targetRows)
                {
                    // Skip deleted rows. The "IsMatchedAcrossAccounts" flag used to gate this
                    // loop, but that excluded basket-linked rows whose only linkage is
                    // Reconciliation_Num (no shared BGPMT/InvRef). The fan-out below now
                    // groups by Reconciliation_Num too, so a row is eligible if it has ANY
                    // linkable key.
                    if (row.IsDeleted) continue;

                    bool hasInvRef = !string.IsNullOrWhiteSpace(row.InternalInvoiceReference);
                    bool hasBgpmt = !string.IsNullOrWhiteSpace(row.DWINGS_BGPMT);
                    bool hasRecoNum = !string.IsNullOrWhiteSpace(row.Reconciliation_Num);
                    if (!hasInvRef && !hasBgpmt && !hasRecoNum) continue;

                    // Find all related rows by ANY of the three linkage keys. The third key
                    // (Reconciliation_Num) is essential when the user has linked rows via the
                    // basket — at that point pivot and receivable share the basket-stamped
                    // group ref but each receivable can still carry its own BGPMT.
                    var groupRows = allData.Where(r =>
                        !r.IsDeleted
                        && ((hasInvRef && string.Equals(r.InternalInvoiceReference?.Trim(), row.InternalInvoiceReference?.Trim(), StringComparison.OrdinalIgnoreCase))
                            || (hasBgpmt && string.Equals(r.DWINGS_BGPMT?.Trim(), row.DWINGS_BGPMT?.Trim(), StringComparison.OrdinalIgnoreCase))
                            || (hasRecoNum && string.Equals(r.Reconciliation_Num?.Trim(), row.Reconciliation_Num?.Trim(), StringComparison.OrdinalIgnoreCase)))
                    ).ToList();

                    // ── Phase 2: skip the group if it is not fully collected yet ──
                    // IsPartiallyPaid prefers the user's manual override, otherwise it falls
                    // back to the auto group balance (sum of SignedAmount across receivables
                    // and matched pivots). Either way, a non-zero effective remaining means
                    // money is still expected on this BGI and the Trigger must wait.
                    var partial = groupRows.FirstOrDefault(r => r.IsPartiallyPaid);
                    if (partial != null)
                    {
                        var key = !string.IsNullOrWhiteSpace(row.DWINGS_BGPMT) ? row.DWINGS_BGPMT
                                : !string.IsNullOrWhiteSpace(row.InternalInvoiceReference) ? row.InternalInvoiceReference
                                : row.ID;
                        // Show whichever value the rule used: override wins when present, else auto.
                        var owed = partial.EffectiveRemaining;
                        var src = partial.HasManualRemainingOverride ? "override" : "auto";
                        partiallyPaidSkipped.Add($"{key} (still owed: {owed?.ToString("N2", CultureInfo.InvariantCulture)} [{src}])");
                        continue;
                    }

                    // Track ALL linked IDs for TRIGGER DONE expansion after success
                    foreach (var g in groupRows)
                        allGroupIds.Add(g.ID);

                    // Identify receivable + pivot sides from the GROUP (not re-searched by BGPMT)
                    var recvLines = groupRows.Where(r => string.Equals(r.Account_ID, receivableId, StringComparison.OrdinalIgnoreCase)).ToList();
                    var pivotLines = groupRows.Where(r => !string.Equals(r.Account_ID, receivableId, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (recvLines.Count == 0 || pivotLines.Count == 0) continue;

                    // Distinct BGPMTs in the group (always taken from the receivable side —
                    // pivots typically don't carry BGPMT). The DWINGS API fires once per BGPMT.
                    var distinctBgpmts = recvLines
                        .Where(r => !string.IsNullOrWhiteSpace(r.DWINGS_BGPMT))
                        .Select(r => r.DWINGS_BGPMT.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (distinctBgpmts.Count == 0) continue; // BGPMT is required for the DWINGS API

                    // Pre-compute the canonical PaymentRef for this group: pivot's
                    // Reconciliation_Num is the business-mandated externalReference. We fall
                    // back to InternalInvoiceReference / pivot.PaymentReference / Pivot_TRNFromLabel
                    // only when the pivot has no usable Reconciliation_Num.
                    string groupPayRef = pivotLines
                            .Where(p => p.Reconciliation_Num?.Length > 3)
                            .Select(p => p.Reconciliation_Num.Trim())
                            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                        ?? (hasInvRef ? row.InternalInvoiceReference?.Trim() : null)
                        ?? pivotLines.Select(p => p.PaymentReference).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                        ?? pivotLines.Select(p => p.Pivot_TRNFromLabel).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                        ?? string.Empty;

                    foreach (var bgpmt in distinctBgpmts)
                    {
                        if (processedBgpmts.Contains(bgpmt)) continue;
                        processedBgpmts.Add(bgpmt);

                        // For each unique BGPMT in this group, the AllIds payload includes
                        // EVERY linked id (all receivables + all pivots in the group). When a
                        // single DWINGS call succeeds, the fan-out below marks every linked
                        // line as TRIGGER DONE — even receivables whose own BGPMT call may
                        // have failed (e.g. duplicate-trigger errors on shared BGPMTs).
                        var groupIds = recvLines.Select(r => r.ID).Concat(pivotLines.Select(r => r.ID)).ToList();

                        var recvFirst = recvLines.First(rl => string.Equals(rl.DWINGS_BGPMT?.Trim(), bgpmt, StringComparison.OrdinalIgnoreCase));
                        triggerItems.Add((
                            AllIds: string.Join(",", groupIds),
                            PaymentRef: groupPayRef,
                            ValueDate: pivotLines.Select(p => p.Value_Date).FirstOrDefault(),
                            RequestedAmount: recvFirst.I_REQUESTED_INVOICE_AMOUNT,
                            Currency: recvFirst.I_BILLING_CURRENCY,
                            Bgpmt: bgpmt,
                            IsGrouped: groupRows.Any(r => r.IsMatchedAcrossAccounts),
                            SourceRow: recvFirst
                        ));
                    }
                }

                // Phase 2: tell the operator about anything we deferred BEFORE the count check —
                // they need this signal even when nothing was triggerable in the rest of the
                // selection (otherwise the silent no-op looks like a bug).
                if (partiallyPaidSkipped.Count > 0)
                {
                    ShowToast($"Skipped {partiallyPaidSkipped.Count} partially-paid group(s):\n" +
                              string.Join("\n", partiallyPaidSkipped.Take(8)) +
                              (partiallyPaidSkipped.Count > 8 ? "\n…" : string.Empty));
                }

                if (triggerItems.Count == 0)
                {
                    if (partiallyPaidSkipped.Count == 0)
                        ShowToast("No eligible DWINGS trigger rows in selection.");
                    return;
                }

                // ---- validate / prompt for missing PaymentReference ----
                for (int i = 0; i < triggerItems.Count; i++)
                {
                    var item = triggerItems[i];
                    if (string.IsNullOrWhiteSpace(item.PaymentRef))
                    {
                        var title = $"Payment Reference for {item.SourceRow.DWINGS_BGPMT ?? item.SourceRow.InternalInvoiceReference}";
                        var input = ShowTextInputDialog(title);
                        if (string.IsNullOrWhiteSpace(input))
                        {
                            ShowToast("Cancelled \u2014 Payment Reference required.");
                            return;
                        }
                        triggerItems[i] = (item.AllIds, input.Trim(), item.ValueDate,
                                           item.RequestedAmount, item.Currency, item.Bgpmt,
                                           item.IsGrouped, item.SourceRow);
                    }
                }

                // ---- confirmation ----
                if (MessageBox.Show(
                    $"Process DWINGS Blue Button for {triggerItems.Count} group(s)?\n\n" +
                    string.Join("\n", triggerItems.Select(t => $"  • {t.Bgpmt} | Ref: {t.PaymentRef}")),
                    "Confirm DWINGS Process",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                // ---- process ----
                Mouse.OverrideCursor = Cursors.Wait;
                var dw = new Dwings();
                dw.GetUserInfo();

                int successCount = 0;
                var allUpdates = new List<Reconciliation>();
                var messages = new List<string>();

                foreach (var item in triggerItems)
                {
                    try
                    {
                        var (ok, msg) = dw.Dwings_PressBlueButton(
                            item.PaymentRef,
                            item.ValueDate ?? DateTime.UtcNow,
                            item.RequestedAmount,
                            country.CNT_DWID,
                            item.Currency,
                            item.Bgpmt);

                        if (ok)
                        {
                            var ids = item.AllIds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                            var bgpmtUpdates = await MarkTriggerDoneAsync(ids);
                            if (!item.IsGrouped && !string.IsNullOrWhiteSpace(item.PaymentRef))
                                foreach (var u in bgpmtUpdates) u.PaymentReference = item.PaymentRef;
                            allUpdates.AddRange(bgpmtUpdates);
                            successCount++;
                            messages.Add($"OK: {item.Bgpmt}");
                        }
                        else
                        {
                            messages.Add($"FAIL ({item.Bgpmt}): {msg}");
                        }
                    }
                    catch (Exception ex)
                    {
                        messages.Add($"ERROR ({item.Bgpmt}): {ex.Message}");
                    }
                }

                Mouse.OverrideCursor = null;

                // ---- persist: expand TRIGGER DONE to ALL linked/grouped rows ----
                // We expand whenever at least one DWINGS call succeeded for the group OR
                // when the operator confirmed the group was already triggered upstream
                // (handled implicitly by relying on successCount > 0). Receivables that
                // share a BGPMT with another succeeded receivable are still marked DONE
                // through this expansion, which is the requested behaviour for the
                // "1 pivot ↔ 80 receivables" / shared-BGPMT scenario.
                if (allUpdates.Count > 0)
                {
                    var alreadyUpdatedIds = new HashSet<string>(allUpdates.Select(u => u.ID ?? ""), StringComparer.OrdinalIgnoreCase);
                    var expansionUpdates = await MarkTriggerDoneAsync(allGroupIds, alreadyUpdatedIds);
                    allUpdates.AddRange(expansionUpdates);

                    await _reconciliationService.SaveReconciliationsAsync(allUpdates, applyRulesOnEdit: false);
                }

                // ---- refresh UI: mirror the full TRIGGER DONE payload onto the view rows
                //      so the grid reflects KPI + ReasonNonRisky + RiskyItem immediately,
                //      not only Action/ActionStatus/ActionDate as before.
                if (allUpdates.Count > 0)
                {
                    var triggerActionIdUi = (int)ActionType.Trigger;
                    var nowUi = DateTime.UtcNow;
                    var paidNotReconciledKpi = (int)KPIType.PaidButNotReconciled;
                    var commissionsCollectedReason = (int)Risky.CollectedCommissionsCredit67P;
                    foreach (var viewRow in allData.Where(r => allGroupIds.Contains(r.ID)))
                    {
                        viewRow.Action = triggerActionIdUi;
                        viewRow.ActionStatus = true;
                        viewRow.ActionDate = nowUi;
                        viewRow.KPI = paidNotReconciledKpi;
                        viewRow.ReasonNonRisky = commissionsCollectedReason;
                        viewRow.RiskyItem = false;
                    }
                }
                AfterSave(raiseDataChanged: true);

                ShowToast($"DWINGS: {successCount}/{triggerItems.Count} processed.\n" + string.Join("\n", messages));
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                ShowError($"DWINGS process failed: {ex.Message}");
            }
        }

        private void AddToLinkingBasket_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rows = GetTargetRows(sender);
                if (rows.Count == 0) return;
                AddToLinkingBasketRequested?.Invoke(rows);
                ShowToast($"🔗 {rows.Count} row(s) added to linking basket");
            }
            catch (Exception ex)
            {
                ShowError($"Failed to add to basket: {ex.Message}");
            }
        }
    }
}
