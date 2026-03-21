using System;
using System.Collections.Generic;
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

        // Open full conversation and allow appending a new comment line
        private async void CommentsCell_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var fe = sender as FrameworkElement;
                var row = fe?.DataContext as ReconciliationViewData;
                if (row == null || _reconciliationService == null) return;

                var dlg = new CommentsDialog
                {
                    Owner = Window.GetWindow(this)
                };
                dlg.SetUsers(AssigneeOptions);
                dlg.SetConversationText(row.Comments ?? string.Empty);
                var res = dlg.ShowDialog();
                if (res == true)
                {
                    var user = _reconciliationService.CurrentUser;
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
                 * 1️⃣  Vérification multi‑utilisateur
                 * -----------------------------------------------------------------*/
                if (!await CheckMultiUserBeforeEditAsync())
                    return;

                /* -----------------------------------------------------------------
                 * 2️⃣  Récupération du MenuItem et de la ligne concernée
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
                var dg = this.FindName("ResultsDataGrid") as DataGrid;
                var targetRows = new List<ReconciliationViewData>();

                if (dg?.SelectedItems != null && dg.SelectedItems.Count > 1)
                    targetRows.AddRange(dg.SelectedItems.OfType<ReconciliationViewData>());
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
                    if (isAction)
                    {
                        UserFieldUpdateService.ApplyAction(r, reco, newId, AllUserFields);
                        ViewDataEnricher.RefreshUserFieldDisplay(r, "Action");
                    }
                    else if (isKpi)
                    {
                        UserFieldUpdateService.ApplyKpi(r, reco, newId);
                        ViewDataEnricher.RefreshUserFieldDisplay(r, "KPI");
                    }
                    else if (isInc)
                    {
                        UserFieldUpdateService.ApplyIncidentType(r, reco, newId);
                        ViewDataEnricher.RefreshUserFieldDisplay(r, "Incident Type");
                    }
                    else if (isReasonNonRisky)                      // ---- Reason Non‑Risky ----
                    {
                        r.ReasonNonRisky = newId;
                        reco.ReasonNonRisky = newId;
                        ViewDataEnricher.RefreshUserFieldDisplay(r, "ReasonNonRisky");
                        // Si vous avez un helper dédié pour rafraîchir l’icône, appelez‑le ici.
                    }

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
                                    var curUser = Environment.UserName;
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
                // Check multi-user before editing
                if (!await CheckMultiUserBeforeEditAsync())
                    return;

                var dg = this.FindName("ResultsDataGrid") as DataGrid;
                if (dg == null || _reconciliationService == null) return;

                var selected = dg.SelectedItems?.OfType<ReconciliationViewData>().ToList() ?? new List<ReconciliationViewData>();
                if (selected.Count == 0) return;

                // Build an ad-hoc prompt window for comment input
                var prompt = new Window
                {
                    Title = $"Set Comment for {selected.Count} row(s)",
                    Width = 480,
                    Height = 220,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Window.GetWindow(this),
                    ResizeMode = ResizeMode.NoResize,
                    Content = null
                };

                var grid = new Grid { Margin = new Thickness(12) };
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tb = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                Grid.SetRow(tb, 0);
                var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
                var btnOk = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
                var btnCancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
                btnOk.Click += (s, ea) => { prompt.DialogResult = true; prompt.Close(); };
                btnCancel.Click += (s, ea) => { prompt.DialogResult = false; prompt.Close(); };
                panel.Children.Add(btnOk);
                panel.Children.Add(btnCancel);
                Grid.SetRow(panel, 1);
                grid.Children.Add(tb);
                grid.Children.Add(panel);
                prompt.Content = grid;

                var result = prompt.ShowDialog();
                if (result != true) return;
                var text = tb.Text ?? string.Empty;

                var updates = new List<Reconciliation>();
                var user = _reconciliationService?.CurrentUser ?? Environment.UserName;
                string prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {user}: ";
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
                
                // Refresh KPIs to reflect changes immediately
                UpdateKpis(_filteredData);

                // Refresh @mention badge (comment may contain new mentions)
                try { RefreshMentionBadge(); } catch { }

                // Background sync best effort (debounced)
                try
                {
                    ScheduleBulkPushDebounced();
                }
                catch { }
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
                // Check multi-user before editing
                if (!await CheckMultiUserBeforeEditAsync())
                    return;

                var dg = this.FindName("ResultsDataGrid") as DataGrid;
                if (dg == null || _reconciliationService == null) return;
                var rowCtx = (sender as FrameworkElement)?.DataContext as ReconciliationViewData;
                var targetRows = dg.SelectedItems.Count > 1 && rowCtx != null && dg.SelectedItems.OfType<ReconciliationViewData>().Contains(rowCtx)
                    ? dg.SelectedItems.OfType<ReconciliationViewData>().ToList()
                    : (rowCtx != null ? new List<ReconciliationViewData> { rowCtx } : new List<ReconciliationViewData>());
                if (targetRows.Count == 0) return;

                var updates = new List<Reconciliation>();
                foreach (var r in targetRows)
                {
                    if (!r.Action.HasValue) continue; // only set status if an action exists
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);
                    r.ActionStatus = isDone; 
                    r.ActionDate = DateTime.Now;
                    reco.ActionStatus = isDone;
                    reco.ActionDate = r.ActionDate;
                    updates.Add(reco);
                }
                if (updates.Count == 0) return;
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);
                
                // Refresh KPIs to reflect changes immediately
                UpdateKpis(_filteredData);
                
                try { ScheduleBulkPushDebounced(); } catch { }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to mark action as {statusLabel}: {ex.Message}");
            }
        }

        // Quick take ownership
        private async void QuickTakeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check multi-user before editing
                if (!await CheckMultiUserBeforeEditAsync())
                    return;

                var dg = this.FindName("ResultsDataGrid") as DataGrid;
                if (dg == null) return;
                var rowCtx = (sender as FrameworkElement)?.DataContext as ReconciliationViewData;
                var targetRows = dg.Items.OfType<ReconciliationViewData>().ToList();
                if (targetRows.Count == 0) return;

                var updates = new List<Reconciliation>();
                foreach (var r in targetRows)
                {
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);
                    var user = _reconciliationService.CurrentUser;
                    r.Assignee = user;
                    reco.Assignee = user;
                    updates.Add(reco);
                }
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);

                // Schedule debounced background sync
                try
                {
                    ScheduleBulkPushDebounced();
                }
                catch { }
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
                // Check multi-user before editing
                if (!await CheckMultiUserBeforeEditAsync())
                    return;

                var dg = this.FindName("ResultsDataGrid") as DataGrid;
                if (dg == null) return;
                var rowCtx = (sender as FrameworkElement)?.DataContext as ReconciliationViewData;
                var targetRows = dg.SelectedItems.Count > 1 && rowCtx != null && dg.SelectedItems.OfType<ReconciliationViewData>().Contains(rowCtx)
                    ? dg.SelectedItems.OfType<ReconciliationViewData>().ToList()
                    : (rowCtx != null ? new List<ReconciliationViewData> { rowCtx } : new List<ReconciliationViewData>());
                if (targetRows.Count == 0) return;

                // Prompt date selection
                var prompt = new Window
                {
                    Title = "Set Reminder Date",
                    Width = 320,
                    Height = 160,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Window.GetWindow(this),
                    ResizeMode = ResizeMode.NoResize
                };
                var grid = new Grid { Margin = new Thickness(10) };
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var datePicker = new DatePicker { SelectedDate = DateTime.Today };
                Grid.SetRow(datePicker, 0);
                grid.Children.Add(datePicker);
                var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
                var btnOk = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
                var btnCancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
                btnOk.Click += (s, ea) => { prompt.DialogResult = true; prompt.Close(); };
                btnCancel.Click += (s, ea) => { prompt.DialogResult = false; prompt.Close(); };
                panel.Children.Add(btnOk);
                panel.Children.Add(btnCancel);
                Grid.SetRow(panel, 1);
                grid.Children.Add(panel);
                prompt.Content = grid;
                var res = prompt.ShowDialog();
                if (res != true) return;
                var selDate = datePicker.SelectedDate;
                if (!selDate.HasValue) return;

                var updates = new List<Reconciliation>();
                var currentUser = _reconciliationService.CurrentUser;
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
                    updates.Add(reco);
                }
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);
                
                // Refresh KPIs to reflect changes immediately
                UpdateKpis(_filteredData);

                // Background sync best effort (debounced)
                try
                {
                    ScheduleBulkPushDebounced();
                }
                catch { }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to set reminder: {ex.Message}");
            }
        }
        // Quick set First Claim Date = user‑chosen date (or cancel)
        private async void QuickSetFirstClaimTodayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1️⃣ Vérifier le mode multi‑utilisateur avant toute édition
                if (!await CheckMultiUserBeforeEditAsync())
                    return;

                // 2️⃣ Récupérer la DataGrid et les lignes concernées
                var dg = this.FindName("ResultsDataGrid") as DataGrid;
                if (dg == null) return;

                var rowCtx = (sender as FrameworkElement)?.DataContext as ReconciliationViewData;
                var targetRows =
                    dg.SelectedItems?.Count > 1 && rowCtx != null && dg.SelectedItems.OfType<ReconciliationViewData>().Contains(rowCtx)
                        ? dg.SelectedItems.OfType<ReconciliationViewData>().ToList()
                        : (rowCtx != null ? new List<ReconciliationViewData> { rowCtx } : new List<ReconciliationViewData>());

                if (targetRows.Count == 0) return;

                // 3️⃣ Ouvrir un dialogue de sélection de date
                DateTime? chosenDate = ShowDatePickerDialog();
                if (!chosenDate.HasValue)               // L'utilisateur a annulé
                    return;                             // → on ne touche à rien

                // 4️⃣ Préparer les entités à mettre à jour
                var updates = new List<Reconciliation>();
                foreach (var r in targetRows)
                {
                    // Récupérer (ou créer) le POCO métier depuis le service
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);

                    r.FirstClaimDate = chosenDate.Value;
                    reco.FirstClaimDate = chosenDate.Value;

                    updates.Add(reco);
                }

                // 5️⃣ Persister les modifications (sans déclencher les règles de validation)
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);

                // 6️⃣ Rafraîchir les KPI pour refléter immédiatement la modification
                UpdateKpis(_filteredData);

                // 7️⃣ Lancer la synchronisation en arrière‑plan (dé‑bouncée)
                try
                {
                    ScheduleBulkPushDebounced();
                }
                catch { /* Ignorer les exceptions de sync best‑effort */ }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to set First Claim Date: {ex.Message}");
            }
        }
        // Quick set First Claim Date = user‑chosen date (or cancel)
        private async void QuickSetLastClaimTodayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1️⃣ Vérifier le mode multi‑utilisateur avant toute édition
                if (!await CheckMultiUserBeforeEditAsync())
                    return;

                // 2️⃣ Récupérer la DataGrid et les lignes concernées
                var dg = this.FindName("ResultsDataGrid") as DataGrid;
                if (dg == null) return;

                var rowCtx = (sender as FrameworkElement)?.DataContext as ReconciliationViewData;
                var targetRows =
                    dg.SelectedItems?.Count > 1 && rowCtx != null && dg.SelectedItems.OfType<ReconciliationViewData>().Contains(rowCtx)
                        ? dg.SelectedItems.OfType<ReconciliationViewData>().ToList()
                        : (rowCtx != null ? new List<ReconciliationViewData> { rowCtx } : new List<ReconciliationViewData>());

                if (targetRows.Count == 0) return;

                // 3️⃣ Ouvrir un dialogue de sélection de date
                DateTime? chosenDate = ShowDatePickerDialog();
                if (!chosenDate.HasValue)               // L'utilisateur a annulé
                    return;                             // → on ne touche à rien

                // 4️⃣ Préparer les entités à mettre à jour
                var updates = new List<Reconciliation>();
                foreach (var r in targetRows)
                {
                    // Récupérer (ou créer) le POCO métier depuis le service
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(r.ID);

                    // Si la première date était déjà renseignée, on met à jour la 2ᵉ date,
                    // sinon on remplisse la première date.
                    if (!r.FirstClaimDate.HasValue)
                    {
                        r.FirstClaimDate = chosenDate.Value;
                        reco.FirstClaimDate = chosenDate.Value;
                    }

                    r.LastClaimDate = chosenDate.Value;
                    reco.LastClaimDate = chosenDate.Value;

                    updates.Add(reco);
                }

                // 5️⃣ Persister les modifications (sans déclencher les règles de validation)
                await _reconciliationService.SaveReconciliationsAsync(updates, applyRulesOnEdit: false);

                // 6️⃣ Rafraîchir les KPI pour refléter immédiatement la modification
                UpdateKpis(_filteredData);

                // 7️⃣ Lancer la synchronisation en arrière‑plan (dé‑bouncée)
                try
                {
                    ScheduleBulkPushDebounced();
                }
                catch { /* Ignorer les exceptions de sync best‑effort */ }
            }
            catch (Exception ex)
            {
                ShowError($"Failed to set First Claim Date: {ex.Message}");
            }
        }

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
                if (!await CheckMultiUserBeforeEditAsync()) return;

                var country = CurrentCountryObject;
                if (country == null) { ShowError("No country loaded."); return; }
                var receivableId = country.CNT_AmbreReceivable;

                // ---- target rows (single or multi-select) ----
                var dg = this.FindName("ResultsDataGrid") as DataGrid;
                var rowCtx = (sender as FrameworkElement)?.DataContext as ReconciliationViewData;
                var targetRows = dg?.SelectedItems?.Count > 1
                                 && rowCtx != null
                                 && dg.SelectedItems.OfType<ReconciliationViewData>().Contains(rowCtx)
                    ? dg.SelectedItems.OfType<ReconciliationViewData>().ToList()
                    : (rowCtx != null ? new List<ReconciliationViewData> { rowCtx } : new List<ReconciliationViewData>());

                if (targetRows.Count == 0) return;

                // ---- build trigger items (same logic as DwingsButtonsWindow.LoadDataAsync) ----
                // We need the full dataset to find matching pivots
                var allData = _allViewData ?? _filteredData ?? ViewData?.ToList() ?? new List<ReconciliationViewData>();
                var triggerItems = new List<(string AllIds, string PaymentRef, DateTime? ValueDate,
                                             string RequestedAmount, string Currency, string Bgpmt,
                                             bool IsGrouped, ReconciliationViewData SourceRow)>();

                foreach (var row in targetRows)
                {
                    // Only process rows that are not deleted and are grouped
                    if (row.IsDeleted || !row.IsMatchedAcrossAccounts)
                        continue;

                    // Determine grouping key
                    bool hasInvRef = !string.IsNullOrWhiteSpace(row.InternalInvoiceReference);
                    bool hasBgpmt = !string.IsNullOrWhiteSpace(row.DWINGS_BGPMT);
                    if (!hasInvRef && !hasBgpmt) continue;

                    string keyType = hasInvRef ? "INV" : "BGPMT";
                    string keyValue = hasInvRef ? row.InternalInvoiceReference.Trim() : row.DWINGS_BGPMT.Trim();

                    // Determine if clicked row is receivable
                    bool isReceivable = string.Equals(row.Account_ID, receivableId, StringComparison.OrdinalIgnoreCase);

                    // Find the receivable(s) and pivot(s) for this group
                    var groupRows = allData.Where(r =>
                        !r.IsDeleted
                        && (keyType == "INV"
                            ? string.Equals(r.InternalInvoiceReference, keyValue, StringComparison.OrdinalIgnoreCase)
                            : string.Equals(r.DWINGS_BGPMT, keyValue, StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                    var recvLines = groupRows.Where(r => string.Equals(r.Account_ID, receivableId, StringComparison.OrdinalIgnoreCase)).ToList();
                    var pivotLines = groupRows.Where(r => !string.Equals(r.Account_ID, receivableId, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (recvLines.Count == 0 || pivotLines.Count == 0) continue;

                    // Build combined IDs list (receivable + pivots)
                    var allIds = recvLines.Select(r => r.ID).Concat(pivotLines.Select(r => r.ID)).ToList();

                    // PaymentReference: prefer pivot's Reconciliation_Num > PaymentReference > Pivot_TRNFromLabel
                    string payRef = pivotLines
                        .Where(p => p.Reconciliation_Num?.Length > 3)
                        .Select(p => p.Reconciliation_Num)
                        .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                        ?? pivotLines.Select(p => p.PaymentReference).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                        ?? pivotLines.Select(p => p.Pivot_TRNFromLabel).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                        ?? string.Empty;

                    var recvFirst = recvLines.First();
                    triggerItems.Add((
                        AllIds: string.Join(",", allIds),
                        PaymentRef: payRef,
                        ValueDate: pivotLines.Select(p => p.Value_Date).FirstOrDefault(),
                        RequestedAmount: recvFirst.I_REQUESTED_INVOICE_AMOUNT,
                        Currency: recvFirst.I_BILLING_CURRENCY,
                        Bgpmt: recvFirst.DWINGS_BGPMT ?? string.Empty,
                        IsGrouped: groupRows.Any(r => r.IsMatchedAcrossAccounts),
                        SourceRow: recvFirst
                    ));
                }

                if (triggerItems.Count == 0)
                {
                    ShowToast("No eligible DWINGS trigger rows in selection.");
                    return;
                }

                // ---- validate / prompt for missing PaymentReference ----
                for (int i = 0; i < triggerItems.Count; i++)
                {
                    var item = triggerItems[i];
                    if (string.IsNullOrWhiteSpace(item.PaymentRef))
                    {
                        var prompt = new Window
                        {
                            Title = $"Payment Reference for {item.SourceRow.DWINGS_BGPMT ?? item.SourceRow.InternalInvoiceReference}",
                            Width = 400, Height = 150,
                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                            Owner = Window.GetWindow(this),
                            ResizeMode = ResizeMode.NoResize
                        };
                        var grid = new Grid { Margin = new Thickness(10) };
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        var tb = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
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

                        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(tb.Text))
                        {
                            ShowToast("Cancelled — Payment Reference required.");
                            return;
                        }
                        triggerItems[i] = (item.AllIds, tb.Text.Trim(), item.ValueDate,
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
                            foreach (var id in ids)
                            {
                                var reco = await _reconciliationService.GetReconciliationByIdAsync(_currentCountryId, id.Trim());
                                if (reco != null)
                                {
                                    reco.ActionStatus = true;
                                    reco.TriggerDate = DateTime.UtcNow;
                                    if (!item.IsGrouped && !string.IsNullOrWhiteSpace(item.PaymentRef))
                                        reco.PaymentReference = item.PaymentRef;
                                    allUpdates.Add(reco);
                                }
                            }
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

                // ---- persist ----
                if (allUpdates.Count > 0)
                {
                    await _reconciliationService.SaveReconciliationsAsync(allUpdates, applyRulesOnEdit: false);
                    try { ScheduleBulkPushDebounced(); } catch { }
                }

                // ---- refresh UI ----
                // Update in-memory rows to reflect ActionStatus change
                foreach (var item in triggerItems)
                {
                    var ids = item.AllIds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var viewRow in allData.Where(r => ids.Contains(r.ID)))
                    {
                        viewRow.ActionStatus = true;
                    }
                }
                UpdateKpis(_filteredData);
                DataChanged?.Invoke();

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
                var dg = this.FindName("ResultsDataGrid") as System.Windows.Controls.DataGrid;
                if (dg == null) return;

                var rowCtx = (sender as FrameworkElement)?.DataContext as ReconciliationViewData;
                var rows = dg.SelectedItems?.Count > 1 && rowCtx != null && dg.SelectedItems.OfType<ReconciliationViewData>().Contains(rowCtx)
                    ? dg.SelectedItems.OfType<ReconciliationViewData>().ToList()
                    : (rowCtx != null ? new List<ReconciliationViewData> { rowCtx } : new List<ReconciliationViewData>());

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
