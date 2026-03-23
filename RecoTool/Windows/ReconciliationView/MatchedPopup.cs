using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RecoTool.Services.DTOs;

namespace RecoTool.Windows
{
    // Partial: Matched rows popup logic extracted from ReconciliationView.xaml.cs
    public partial class ReconciliationView
    {
        // Reusable popup window and its inner view
        private static Window _groupPopupWindow; // fallback generic (kept for safety)
        private static ReconciliationView _groupPopupView;
        // Dedicated popups per account side
        private static Window _pivotPopupWindow;
        private static ReconciliationView _pivotPopupView;
        private static Window _receivablePopupWindow;
        private static ReconciliationView _receivablePopupView;

        // Instance-level marker: set when this view is hosted inside a dedicated popup window.
        // 'P' => Pivot popup, 'R' => Receivable popup, null => main view or generic.
        private char? _popupDedicatedSide;

        private void OpenMatchedPopup(ReconciliationViewData rowData)
        {
            try
            {
                if (rowData == null) return;

                // Build backend WHERE clause using OR of ALL available grouping keys.
                // A row can be matched across accounts via any of these keys, so we must
                // include all of them to guarantee we find the counterpart rows.
                var clauses = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(rowData.DWINGS_InvoiceID))
                    clauses.Add($"r.DWINGS_InvoiceID = '{rowData.DWINGS_InvoiceID.Replace("'", "''")}'");
                if (!string.IsNullOrWhiteSpace(rowData.InternalInvoiceReference))
                    clauses.Add($"r.InternalInvoiceReference = '{rowData.InternalInvoiceReference.Replace("'", "''")}'");
                if (!string.IsNullOrWhiteSpace(rowData.DWINGS_BGPMT))
                    clauses.Add($"r.DWINGS_BGPMT = '{rowData.DWINGS_BGPMT.Replace("'", "''")}'");
                // Last-resort fallback: Event_Num (only when no stronger key exists)
                if (clauses.Count == 0 && !string.IsNullOrWhiteSpace(rowData.Event_Num))
                    clauses.Add($"a.Event_Num = '{rowData.Event_Num.Replace("'", "''")}'");

                if (clauses.Count == 0) return;
                string where = clauses.Count == 1 ? clauses[0] : $"({string.Join(" OR ", clauses)})";

                // Try to hook existing windows by Tag in case static refs were lost
                try
                {
                    Window FindExisting(object tag)
                    {
                        try
                        {
                            foreach (Window w in Application.Current.Windows)
                            {
                                if (w != null && Equals(w.Tag, tag)) return w;
                            }
                        }
                        catch { }
                        return null;
                    }
                    var exPivot = FindExisting("PivotGroup");
                    if ((_pivotPopupWindow == null || !_pivotPopupWindow.IsVisible) && exPivot != null)
                    {
                        _pivotPopupWindow = exPivot;
                        try { _pivotPopupView = exPivot.Content as ReconciliationView; } catch { _pivotPopupView = null; }
                    }
                    var exRecv = FindExisting("ReceivableGroup");
                    if ((_receivablePopupWindow == null || !_receivablePopupWindow.IsVisible) && exRecv != null)
                    {
                        _receivablePopupWindow = exRecv;
                        try { _receivablePopupView = exRecv.Content as ReconciliationView; } catch { _receivablePopupView = null; }
                    }
                    var exGroup = FindExisting("GenericGroup");
                    if ((_groupPopupWindow == null || !_groupPopupWindow.IsVisible) && exGroup != null)
                    {
                        _groupPopupWindow = exGroup;
                        try { _groupPopupView = exGroup.Content as ReconciliationView; } catch { _groupPopupView = null; }
                    }
                }
                catch { }

                // Determine the opposite account for the popup view
                string otherAccountId = null;
                string pivot = null;
                string recv = null;
                bool targetIsPivot = false;
                bool targetIsReceivable = false;
                try
                {
                    var country = this.CurrentCountryObject;
                    pivot = country?.CNT_AmbrePivot?.Trim();
                    recv = country?.CNT_AmbreReceivable?.Trim();
                    var currentId = rowData.Account_ID?.Trim();
                    var currentSide = rowData.AccountSide?.Trim();

                    // If we are already inside a dedicated popup, always target the opposite side window
                    bool clickedInPivotPopup = _popupDedicatedSide.HasValue && _popupDedicatedSide.Value == 'P';
                    bool clickedInReceivablePopup = _popupDedicatedSide.HasValue && _popupDedicatedSide.Value == 'R';
                    if (clickedInPivotPopup)
                    {
                        targetIsReceivable = true; otherAccountId = recv;
                    }
                    else if (clickedInReceivablePopup)
                    {
                        targetIsPivot = true; otherAccountId = pivot;
                    }
                    else
                    {
                        // Prefer the already computed AccountSide if present
                        if (string.IsNullOrWhiteSpace(currentSide))
                        {
                            if (!string.IsNullOrWhiteSpace(pivot) && string.Equals(currentId, pivot, StringComparison.OrdinalIgnoreCase))
                                currentSide = "P";
                            else if (!string.IsNullOrWhiteSpace(recv) && string.Equals(currentId, recv, StringComparison.OrdinalIgnoreCase))
                                currentSide = "R";
                        }

                        // From the main view: target the opposite account side
                        if (string.Equals(currentSide, "P", StringComparison.OrdinalIgnoreCase))
                        {
                            targetIsReceivable = true; otherAccountId = recv;
                        }
                        else if (string.Equals(currentSide, "R", StringComparison.OrdinalIgnoreCase))
                        {
                            targetIsPivot = true; otherAccountId = pivot;
                        }
                        else
                        {
                            // Unknown side: default to Receivable
                            targetIsReceivable = true; otherAccountId = recv;
                        }
                    }
                }
                catch { }

                // Select dedicated popup/window per target account side
                Window targetWindow = targetIsPivot ? _pivotPopupWindow : targetIsReceivable ? _receivablePopupWindow : _groupPopupWindow;
                ReconciliationView targetView = targetIsPivot ? _pivotPopupView : targetIsReceivable ? _receivablePopupView : _groupPopupView;
                string titlePrefix = targetIsPivot ? "Group (PIVOT)" : targetIsReceivable ? "Group (RECEIVABLE)" : "Group";

                // Propagate parent's Status filter so popup shows same status (Live/Archived/All)
                var parentStatus = this.FilterStatus;

                // Reuse if already open
                if (targetWindow != null && targetWindow.IsVisible && targetView != null)
                {
                    try { targetView.SyncCountryFromService(refresh: false); } catch { }
                    try { targetView.FilterStatus = parentStatus; } catch { }
                    try { targetView.ApplySavedFilterSql(where); } catch { }
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(otherAccountId))
                            targetView.FilterAccountId = otherAccountId;
                        else
                            targetView.FilterAccountId = null; // show both if unknown
                        targetView.SetViewTitle($"{titlePrefix}: {where}");
                    }
                    catch { }
                    try { targetView.Refresh(); } catch { }
                    try
                    {
                        targetWindow.Activate();
                        targetWindow.Topmost = true; targetWindow.Topmost = false; // bring to front trick
                    }
                    catch { }
                    return;
                }

                // Create and open a dedicated popup for the target side
                targetView = new ReconciliationView(_reconciliationService, _offlineFirstService, _freeApi)
                {
                    Margin = new Thickness(0)
                };
                // Mark the created view with its dedicated side for future reuse detection
                try { targetView._popupDedicatedSide = targetIsPivot ? 'P' : targetIsReceivable ? 'R' : (char?)null; } catch { }
                try { targetView.SyncCountryFromService(refresh: false); } catch { }
                try { targetView.FilterStatus = parentStatus; } catch { }
                try { targetView.ApplySavedFilterSql(where); } catch { }
                try
                {
                    if (!string.IsNullOrWhiteSpace(otherAccountId))
                        targetView.FilterAccountId = otherAccountId;
                    targetView.SetViewTitle($"{titlePrefix}: {where}");
                }
                catch { }
                try { targetView.Refresh(); } catch { }

                targetWindow = new Window
                {
                    Title = $"{titlePrefix} - Related Reconciliations",
                    Content = targetView,
                    Owner = Window.GetWindow(this),
                    Width = 1100,
                    Height = 750,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                // Tag the window for discovery/reuse
                try { targetWindow.Tag = targetIsPivot ? "PivotGroup" : targetIsReceivable ? "ReceivableGroup" : "GenericGroup"; } catch { }
                targetView.CloseRequested += (s, ev) => { try { targetWindow.Close(); } catch { } };
                targetWindow.Closed += (s, e) =>
                {
                    try
                    {
                        if (targetIsPivot) { _pivotPopupWindow = null; _pivotPopupView = null; }
                        else if (targetIsReceivable) { _receivablePopupWindow = null; _receivablePopupView = null; }
                        else { _groupPopupWindow = null; _groupPopupView = null; }
                    }
                    catch { }
                };
                targetWindow.Show();

                // Persist references
                if (targetIsPivot) { _pivotPopupWindow = targetWindow; _pivotPopupView = targetView; }
                else if (targetIsReceivable) { _receivablePopupWindow = targetWindow; _receivablePopupView = targetView; }
                else { _groupPopupWindow = targetWindow; _groupPopupView = targetView; }
            }
            catch { }
        }
    }
}
