using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using RecoTool.Models;
using RecoTool.Services.Analytics;
using RecoTool.Services.DTOs;

namespace RecoTool.Services
{
    /// <summary>
    /// One-click Excel dashboard export for the currently selected country.
    /// Produces a multi-sheet .xlsx file:
    ///   1) Cover          — title, metadata, headline KPIs
    ///   2) KPIs           — detailed numerical breakdown (volumes, status, risk, data quality)
    ///   3) Breakdowns     — Status / Action / KPI / Currency pivots stacked on one sheet
    ///   4) Alerts         — urgent items from DashboardAnalyticsService
    ///   5) Reconciliations — every row with autofilter + freeze pane
    ///
    /// Uses ClosedXML so generation does NOT require Excel to be installed and is fast
    /// on 10k+ rows. Work is offloaded to a background thread; status updates are surfaced
    /// via <see cref="IProgress{String}"/> so the UI can display them live.
    /// </summary>
    public class DashboardExportService
    {
        private readonly ReconciliationService _reconciliationService;
        private readonly OfflineFirstService _offlineFirstService;

        // Branding palette — kept in one place so changing the look-and-feel stays a one-liner.
        private static readonly XLColor HeaderFill = XLColor.FromHtml("#005B46");     // BNP dark green
        private static readonly XLColor HeaderFontColor = XLColor.White;
        private static readonly XLColor SubHeaderFill = XLColor.FromHtml("#E8F5EF");  // soft green tint
        private static readonly XLColor SectionFill = XLColor.FromHtml("#F4F6F8");
        private static readonly XLColor AccentFill = XLColor.FromHtml("#D35400");     // orange accent
        private static readonly XLColor AlertCritical = XLColor.FromHtml("#E74C3C");
        private static readonly XLColor AlertWarning = XLColor.FromHtml("#F39C12");
        private static readonly XLColor AlertInfo = XLColor.FromHtml("#3498DB");

        public DashboardExportService(
            ReconciliationService reconciliationService,
            OfflineFirstService offlineFirstService)
        {
            _reconciliationService = reconciliationService ?? throw new ArgumentNullException(nameof(reconciliationService));
            _offlineFirstService = offlineFirstService ?? throw new ArgumentNullException(nameof(offlineFirstService));
        }

        /// <summary>
        /// Generates the dashboard workbook for the given country and saves it to <paramref name="outputPath"/>.
        /// Returns the absolute path of the written file.
        /// </summary>
        /// <param name="countryId">Country to export. Must be non-empty.</param>
        /// <param name="outputPath">Target .xlsx path (overwritten if it exists).</param>
        /// <param name="progress">Optional progress sink — receives short status messages.</param>
        /// <param name="ct">Cancellation token; honoured between phases.</param>
        public async Task<string> ExportDashboardAsync(
            string countryId,
            string outputPath,
            IProgress<string> progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(countryId))
                throw new ArgumentException("countryId is required", nameof(countryId));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("outputPath is required", nameof(outputPath));

            progress?.Report("Loading reconciliation data…");
            var rows = await _reconciliationService.GetReconciliationViewAsync(countryId, null).ConfigureAwait(false)
                       ?? new List<ReconciliationViewData>();
            ct.ThrowIfCancellationRequested();

            var country = ResolveCountry(countryId);
            var userFields = _offlineFirstService?.UserFields ?? new List<UserField>();
            var ambreSnapshotDate = await TryGetAmbreSnapshotDateAsync(countryId, ct).ConfigureAwait(false);

            progress?.Report($"Building workbook ({rows.Count} rows)…");

            // Workbook generation is purely CPU-bound — push it off the UI thread.
            var finalPath = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using (var wb = new XLWorkbook())
                {
                    wb.Properties.Author = _reconciliationService?.CurrentUser ?? Environment.UserName;
                    wb.Properties.Title = $"Reconciliation Dashboard — {country?.CNT_Id ?? countryId}";
                    wb.Properties.Subject = "Reconciliation export";
                    wb.Properties.Created = DateTime.Now;

                    BuildCoverSheet(wb, country, countryId, rows, ambreSnapshotDate);
                    ct.ThrowIfCancellationRequested();
                    BuildKpiSheet(wb, country, rows);
                    ct.ThrowIfCancellationRequested();
                    BuildBreakdownsSheet(wb, rows, userFields);
                    ct.ThrowIfCancellationRequested();
                    BuildAlertsSheet(wb, rows);
                    ct.ThrowIfCancellationRequested();
                    BuildReconciliationsSheet(wb, rows, userFields);
                    ct.ThrowIfCancellationRequested();

                    var dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // Write to a temp file then move — avoids leaving a half-written file if the user
                    // cancels or the process dies mid-save. ClosedXML validates the extension so the
                    // temp name must still end in .xlsx (it refuses .tmp).
                    var tmp = outputPath + ".tmp.xlsx";
                    wb.SaveAs(tmp);
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Move(tmp, outputPath);
                    return outputPath;
                }
            }, ct).ConfigureAwait(false);

            progress?.Report("Export complete.");
            return finalPath;
        }

        #region Sheet: Cover

        private void BuildCoverSheet(IXLWorkbook wb, Country country, string countryId, List<ReconciliationViewData> rows, DateTime? ambreSnapshotDate)
        {
            var ws = wb.Worksheets.Add("Cover");
            ws.ShowGridLines = false;
            ws.Column(1).Width = 4;   // left margin
            ws.Column(2).Width = 28;
            ws.Column(3).Width = 48;

            // Title band
            var title = ws.Range("B2:C3").Merge();
            title.Value = "Reconciliation Dashboard";
            title.Style.Font.Bold = true;
            title.Style.Font.FontSize = 22;
            title.Style.Font.FontColor = XLColor.White;
            title.Style.Fill.BackgroundColor = HeaderFill;
            title.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            title.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            title.Style.Alignment.Indent = 1;

            // Metadata block
            int r = 5;
            WriteLabelValue(ws, r++, "Country", $"{country?.CNT_Id ?? countryId} — {country?.CNT_Name ?? "(unknown)"}");
            WriteLabelValue(ws, r++, "Generated at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            WriteLabelValue(ws, r++, "Generated by", _reconciliationService?.CurrentUser ?? Environment.UserName);
            WriteLabelValue(ws, r++, "AMBRE snapshot", ambreSnapshotDate.HasValue ? ambreSnapshotDate.Value.ToString("yyyy-MM-dd") : "(not available)");
            WriteLabelValue(ws, r++, "Total rows (live)", rows.Count.ToString("N0", CultureInfo.InvariantCulture));
            r++;

            // Headline KPI cards (3×2 grid)
            var receivableId = country?.CNT_AmbreReceivable;
            var pivotId = country?.CNT_AmbrePivot;
            var receivable = rows.Where(x => x.Account_ID == receivableId).ToList();
            var pivot = rows.Where(x => x.Account_ID == pivotId).ToList();
            int matched = rows.Count(x => !string.IsNullOrWhiteSpace(x.DWINGS_GuaranteeID)
                                       || !string.IsNullOrWhiteSpace(x.DWINGS_InvoiceID)
                                       || !string.IsNullOrWhiteSpace(x.DWINGS_BGPMT));
            double matchedPct = rows.Count > 0 ? matched * 100.0 / rows.Count : 0.0;
            int toReview = rows.Count(x => x.IsToReview);
            int reviewedToday = rows.Count(x => x.IsReviewedToday);
            decimal balance = receivable.Sum(x => x.SignedAmount) + pivot.Sum(x => x.SignedAmount);

            WriteKpiCard(ws, r, 2, "Total rows", rows.Count.ToString("N0", CultureInfo.InvariantCulture), null);
            WriteKpiCard(ws, r, 3, "Matched", $"{matched:N0}  ({matchedPct:N1}%)", matchedPct >= 80 ? XLColor.FromHtml("#148F77") : (matchedPct >= 50 ? XLColor.FromHtml("#B7950B") : AlertCritical));
            r += 3;
            WriteKpiCard(ws, r, 2, "To review", toReview.ToString("N0", CultureInfo.InvariantCulture), toReview > 0 ? AccentFill : null);
            WriteKpiCard(ws, r, 3, "Reviewed today", reviewedToday.ToString("N0", CultureInfo.InvariantCulture), null);
            r += 3;
            WriteKpiCard(ws, r, 2, "Receivable total", FormatAmount(receivable.Sum(x => x.SignedAmount)), null);
            WriteKpiCard(ws, r, 3, "Pivot total", FormatAmount(pivot.Sum(x => x.SignedAmount)), null);
            r += 3;
            WriteKpiCard(ws, r, 2, "Net balance", FormatAmount(balance), Math.Abs(balance) < 0.01m ? XLColor.FromHtml("#148F77") : AlertWarning);

            ws.SheetView.FreezeRows(1);
        }

        private static void WriteLabelValue(IXLWorksheet ws, int row, string label, string value)
        {
            var lc = ws.Cell(row, 2);
            lc.Value = label;
            lc.Style.Font.Bold = true;
            lc.Style.Font.FontColor = XLColor.FromHtml("#566573");
            var vc = ws.Cell(row, 3);
            vc.Value = value ?? string.Empty;
            vc.Style.Font.FontSize = 12;
        }

        private static void WriteKpiCard(IXLWorksheet ws, int row, int col, string label, string value, XLColor valueColor)
        {
            var labelCell = ws.Cell(row, col);
            labelCell.Value = label;
            labelCell.Style.Font.Bold = true;
            labelCell.Style.Font.FontSize = 9;
            labelCell.Style.Font.FontColor = XLColor.FromHtml("#7F8C8D");

            var valueCell = ws.Cell(row + 1, col);
            valueCell.Value = value;
            valueCell.Style.Font.Bold = true;
            valueCell.Style.Font.FontSize = 18;
            if (valueColor != null) valueCell.Style.Font.FontColor = valueColor;

            // Thin bottom border acts as a subtle card divider.
            ws.Cell(row + 2, col).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            ws.Cell(row + 2, col).Style.Border.BottomBorderColor = XLColor.FromHtml("#D5DBDB");
        }

        #endregion

        #region Sheet: KPIs

        private void BuildKpiSheet(IXLWorkbook wb, Country country, List<ReconciliationViewData> rows)
        {
            var ws = wb.Worksheets.Add("KPIs");
            ws.ShowGridLines = false;
            ws.Column(1).Width = 4;
            ws.Column(2).Width = 38;
            ws.Column(3).Width = 20;
            ws.Column(4).Width = 20;

            int r = 2;
            r = WriteSectionTitle(ws, r, "Volumes");
            r = WriteKpiRow(ws, r, "Total rows (live)", rows.Count, null);
            var receivable = rows.Where(x => x.Account_ID == country?.CNT_AmbreReceivable).ToList();
            var pivot = rows.Where(x => x.Account_ID == country?.CNT_AmbrePivot).ToList();
            r = WriteKpiRow(ws, r, "Receivable rows", receivable.Count, null);
            r = WriteKpiRow(ws, r, "Pivot rows", pivot.Count, null);
            r = WriteKpiRow(ws, r, "Receivable total amount", FormatAmount(receivable.Sum(x => x.SignedAmount)), null);
            r = WriteKpiRow(ws, r, "Pivot total amount", FormatAmount(pivot.Sum(x => x.SignedAmount)), null);
            r = WriteKpiRow(ws, r, "Net balance (Receivable + Pivot)", FormatAmount(receivable.Sum(x => x.SignedAmount) + pivot.Sum(x => x.SignedAmount)), null);
            r++;

            r = WriteSectionTitle(ws, r, "Matching status");
            int matched = rows.Count(x => !string.IsNullOrWhiteSpace(x.DWINGS_GuaranteeID)
                                       || !string.IsNullOrWhiteSpace(x.DWINGS_InvoiceID)
                                       || !string.IsNullOrWhiteSpace(x.DWINGS_BGPMT));
            int unmatched = rows.Count - matched;
            r = WriteKpiRow(ws, r, "Matched (has DWINGS link)", matched, Pct(matched, rows.Count));
            r = WriteKpiRow(ws, r, "Unmatched", unmatched, Pct(unmatched, rows.Count));
            r = WriteKpiRow(ws, r, "Matched across accounts", rows.Count(x => x.IsMatchedAcrossAccounts), null);
            r = WriteKpiRow(ws, r, "To review", rows.Count(x => x.IsToReview), null);
            r = WriteKpiRow(ws, r, "Reviewed today", rows.Count(x => x.IsReviewedToday), null);
            r = WriteKpiRow(ws, r, "To remind (overdue)", rows.Count(x => x.ToRemind && x.ToRemindDate.HasValue && x.ToRemindDate.Value.Date < DateTime.Today), null);
            r++;

            r = WriteSectionTitle(ws, r, "Risk & action");
            r = WriteKpiRow(ws, r, "Risky items", rows.Count(x => x.RiskyItem), null);
            r = WriteKpiRow(ws, r, "Action Done", rows.Count(x => x.ActionStatus == true), null);
            r = WriteKpiRow(ws, r, "Action Pending", rows.Count(x => x.ActionStatus == false), null);
            r = WriteKpiRow(ws, r, "Action not set", rows.Count(x => !x.ActionStatus.HasValue), null);
            r++;

            r = WriteSectionTitle(ws, r, "Data quality");
            r = WriteKpiRow(ws, r, "Missing Action", rows.Count(x => !x.Action.HasValue), null);
            r = WriteKpiRow(ws, r, "Missing KPI", rows.Count(x => !x.KPI.HasValue), null);
            r = WriteKpiRow(ws, r, "Missing DWINGS Invoice ID", rows.Count(x => string.IsNullOrWhiteSpace(x.DWINGS_InvoiceID)), null);
            r = WriteKpiRow(ws, r, "Missing DWINGS Guarantee ID", rows.Count(x => string.IsNullOrWhiteSpace(x.DWINGS_GuaranteeID)), null);

            ws.SheetView.FreezeRows(1);
        }

        private static int WriteSectionTitle(IXLWorksheet ws, int row, string title)
        {
            var range = ws.Range(row, 2, row, 4).Merge();
            range.Value = title;
            range.Style.Font.Bold = true;
            range.Style.Font.FontSize = 12;
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Fill.BackgroundColor = HeaderFill;
            range.Style.Alignment.Indent = 1;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Row(row).Height = 22;
            return row + 1;
        }

        private static int WriteKpiRow(IXLWorksheet ws, int row, string label, object value, string extra)
        {
            ws.Cell(row, 2).Value = label;
            ws.Cell(row, 2).Style.Font.FontColor = XLColor.FromHtml("#34495E");

            var vc = ws.Cell(row, 3);
            if (value is string s) vc.Value = s;
            else if (value is int i) vc.Value = i;
            else if (value is decimal dec) vc.Value = dec;
            else vc.Value = value?.ToString() ?? string.Empty;
            vc.Style.Font.Bold = true;
            vc.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            if (!string.IsNullOrEmpty(extra))
            {
                ws.Cell(row, 4).Value = extra;
                ws.Cell(row, 4).Style.Font.FontColor = XLColor.FromHtml("#7F8C8D");
                ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
            return row + 1;
        }

        private static string Pct(int part, int total) => total > 0 ? $"{(part * 100.0 / total):N1} %" : "—";

        #endregion

        #region Sheet: Breakdowns

        private void BuildBreakdownsSheet(IXLWorkbook wb, List<ReconciliationViewData> rows, List<UserField> userFields)
        {
            var ws = wb.Worksheets.Add("Breakdowns");
            ws.ShowGridLines = false;
            ws.Column(1).Width = 3;
            ws.Columns("B:H").Width = 18;

            int r = 2;
            r = WriteSectionTitle(ws, r, "By matching status");
            WriteTableHeader(ws, r, 2, new[] { "Status", "Count", "%" });
            r++;
            var statusRows = BuildStatusBreakdown(rows);
            foreach (var row in statusRows)
            {
                ws.Cell(r, 2).Value = row.label;
                ws.Cell(r, 3).Value = row.count;
                // Excel's '0.0 %' format expects a fraction (0-1), not a percent value.
                ws.Cell(r, 4).Value = rows.Count > 0 ? (double)row.count / rows.Count : 0.0;
                ws.Cell(r, 4).Style.NumberFormat.Format = "0.0 %";
                r++;
            }
            r += 2;

            r = WriteSectionTitle(ws, r, "By Action");
            WriteTableHeader(ws, r, 2, new[] { "Action", "Count", "Sum amount", "# To review" });
            r++;
            foreach (var g in rows
                .Where(x => x.Action.HasValue)
                .GroupBy(x => x.Action.Value)
                .OrderByDescending(g => g.Count()))
            {
                ws.Cell(r, 2).Value = EnumHelper.GetActionName(g.Key, userFields);
                ws.Cell(r, 3).Value = g.Count();
                ws.Cell(r, 4).Value = (double)g.Sum(x => x.SignedAmount);
                ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 5).Value = g.Count(x => x.IsToReview);
                r++;
            }
            // Missing Action row
            var missingAction = rows.Count(x => !x.Action.HasValue);
            if (missingAction > 0)
            {
                ws.Cell(r, 2).Value = "(no action)";
                ws.Cell(r, 2).Style.Font.Italic = true;
                ws.Cell(r, 3).Value = missingAction;
                ws.Cell(r, 4).Value = (double)rows.Where(x => !x.Action.HasValue).Sum(x => x.SignedAmount);
                ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 5).Value = rows.Count(x => !x.Action.HasValue && x.IsToReview);
                r++;
            }
            r += 2;

            r = WriteSectionTitle(ws, r, "By KPI");
            WriteTableHeader(ws, r, 2, new[] { "KPI", "Count", "Sum amount" });
            r++;
            foreach (var g in rows
                .Where(x => x.KPI.HasValue)
                .GroupBy(x => x.KPI.Value)
                .OrderByDescending(g => g.Count()))
            {
                ws.Cell(r, 2).Value = EnumHelper.GetKPIName(g.Key, userFields);
                ws.Cell(r, 3).Value = g.Count();
                ws.Cell(r, 4).Value = (double)g.Sum(x => x.SignedAmount);
                ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                r++;
            }
            r += 2;

            r = WriteSectionTitle(ws, r, "By currency");
            WriteTableHeader(ws, r, 2, new[] { "Currency", "Count", "Receivable amount", "Pivot amount", "Net balance" });
            r++;
            var currentCountry = _offlineFirstService?.CurrentCountry;
            string recvId = currentCountry?.CNT_AmbreReceivable;
            string pivotId = currentCountry?.CNT_AmbrePivot;
            foreach (var g in rows
                .Where(x => !string.IsNullOrWhiteSpace(x.CCY))
                .GroupBy(x => x.CCY.Trim().ToUpperInvariant())
                .OrderByDescending(g => g.Sum(x => Math.Abs(x.SignedAmount))))
            {
                var recvSum = g.Where(x => x.Account_ID == recvId).Sum(x => x.SignedAmount);
                var pivotSum = g.Where(x => x.Account_ID == pivotId).Sum(x => x.SignedAmount);
                ws.Cell(r, 2).Value = g.Key;
                ws.Cell(r, 3).Value = g.Count();
                ws.Cell(r, 4).Value = (double)recvSum;
                ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 5).Value = (double)pivotSum;
                ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(r, 6).Value = (double)(recvSum + pivotSum);
                ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.00";
                r++;
            }
        }

        private static void WriteTableHeader(IXLWorksheet ws, int row, int startCol, string[] headers)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                var c = ws.Cell(row, startCol + i);
                c.Value = headers[i];
                c.Style.Font.Bold = true;
                c.Style.Fill.BackgroundColor = SubHeaderFill;
                c.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                c.Style.Border.BottomBorderColor = XLColor.FromHtml("#A6D5C4");
                c.Style.Alignment.Horizontal = i == 0 ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Right;
            }
        }

        private static List<(string label, int count)> BuildStatusBreakdown(List<ReconciliationViewData> rows)
        {
            int matched = rows.Count(x => !string.IsNullOrWhiteSpace(x.DWINGS_GuaranteeID)
                                       || !string.IsNullOrWhiteSpace(x.DWINGS_InvoiceID)
                                       || !string.IsNullOrWhiteSpace(x.DWINGS_BGPMT));
            int unmatched = rows.Count - matched;
            int toReview = rows.Count(x => x.IsToReview);
            int reviewed = rows.Count(x => x.IsReviewed);
            int risky = rows.Count(x => x.RiskyItem);
            int toRemindOverdue = rows.Count(x => x.ToRemind && x.ToRemindDate.HasValue && x.ToRemindDate.Value.Date < DateTime.Today);
            return new List<(string, int)>
            {
                ("Matched (has DWINGS link)", matched),
                ("Unmatched", unmatched),
                ("To review", toReview),
                ("Reviewed", reviewed),
                ("Risky", risky),
                ("To remind (overdue)", toRemindOverdue),
            };
        }

        #endregion

        #region Sheet: Alerts

        private void BuildAlertsSheet(IXLWorkbook wb, List<ReconciliationViewData> rows)
        {
            var ws = wb.Worksheets.Add("Alerts");
            ws.ShowGridLines = false;
            ws.Column(1).Width = 3;
            ws.Column(2).Width = 14;
            ws.Column(3).Width = 14;
            ws.Column(4).Width = 30;
            ws.Column(5).Width = 80;

            int r = 2;
            var range = ws.Range(r, 2, r, 5).Merge();
            range.Value = "Urgent alerts";
            range.Style.Font.Bold = true;
            range.Style.Font.FontSize = 14;
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Fill.BackgroundColor = HeaderFill;
            range.Style.Alignment.Indent = 1;
            ws.Row(r).Height = 22;
            r += 2;

            var alerts = DashboardAnalyticsService.GetUrgentAlerts(rows) ?? new List<AlertItem>();
            if (alerts.Count == 0)
            {
                ws.Cell(r, 2).Value = "No urgent alerts — dataset is within expected tolerances.";
                ws.Cell(r, 2).Style.Font.Italic = true;
                ws.Cell(r, 2).Style.Font.FontColor = XLColor.FromHtml("#7F8C8D");
                return;
            }

            WriteTableHeader(ws, r, 2, new[] { "Severity", "Count", "Title", "Message" });
            r++;
            foreach (var a in alerts.OrderByDescending(x => x.Priority))
            {
                var sevCell = ws.Cell(r, 2);
                sevCell.Value = a.Type.ToString().ToUpperInvariant();
                sevCell.Style.Font.Bold = true;
                sevCell.Style.Font.FontColor = XLColor.White;
                sevCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                switch (a.Type)
                {
                    case AlertType.Critical: sevCell.Style.Fill.BackgroundColor = AlertCritical; break;
                    case AlertType.Warning:  sevCell.Style.Fill.BackgroundColor = AlertWarning; break;
                    default:                 sevCell.Style.Fill.BackgroundColor = AlertInfo; break;
                }
                ws.Cell(r, 3).Value = a.Count;
                ws.Cell(r, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(r, 4).Value = a.Title ?? string.Empty;
                ws.Cell(r, 4).Style.Font.Bold = true;
                ws.Cell(r, 5).Value = a.Message ?? string.Empty;
                r++;
            }
        }

        #endregion

        #region Sheet: Reconciliations (the data dump)

        private void BuildReconciliationsSheet(IXLWorkbook wb, List<ReconciliationViewData> rows, List<UserField> userFields)
        {
            var ws = wb.Worksheets.Add("Reconciliations");
            ws.ShowGridLines = false;

            // Header row — order matches the reconciliation grid's most-used columns.
            var headers = new[]
            {
                "ID",
                "Account",
                "Operation Date",
                "Value Date",
                "CCY",
                "Signed Amount",
                "Local Amount",
                "Label",
                "Reconciliation #",
                "Internal Invoice Ref",
                "DWINGS Guarantee",
                "DWINGS Invoice",
                "DWINGS BGPMT",
                "Guarantee Type",
                "Guarantee Status",
                "Action",
                "Action Status",
                "Action Date",
                "KPI",
                "Incident Type",
                "Risky",
                "Reason (non-risky)",
                "Inc #",
                "First Claim Date",
                "Last Claim Date",
                "To Remind",
                "Remind Date",
                "Trigger Date",
                "Assignee",
                "Last Comment",
                "Mbaw Data",
                "Created",
                "Last Modified",
                "Modified By",
                "To Review",
                "Reviewed",
            };
            for (int i = 0; i < headers.Length; i++)
            {
                var c = ws.Cell(1, i + 1);
                c.Value = headers[i];
                c.Style.Font.Bold = true;
                c.Style.Font.FontColor = HeaderFontColor;
                c.Style.Fill.BackgroundColor = HeaderFill;
                c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                c.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            }
            ws.Row(1).Height = 22;

            int r = 2;
            foreach (var row in rows)
            {
                int c = 1;
                ws.Cell(r, c++).Value = row.ID ?? string.Empty;
                ws.Cell(r, c++).Value = row.Account_ID ?? string.Empty;
                SetDate(ws.Cell(r, c++), row.Operation_Date);
                SetDate(ws.Cell(r, c++), row.Value_Date);
                ws.Cell(r, c++).Value = row.CCY ?? string.Empty;
                SetDecimal(ws.Cell(r, c++), row.SignedAmount);
                SetDecimal(ws.Cell(r, c++), row.LocalSignedAmount);
                ws.Cell(r, c++).Value = row.RawLabel ?? string.Empty;
                ws.Cell(r, c++).Value = row.Reconciliation_Num ?? string.Empty;
                ws.Cell(r, c++).Value = row.InternalInvoiceReference ?? string.Empty;
                ws.Cell(r, c++).Value = row.DWINGS_GuaranteeID ?? string.Empty;
                ws.Cell(r, c++).Value = row.DWINGS_InvoiceID ?? string.Empty;
                ws.Cell(r, c++).Value = row.DWINGS_BGPMT ?? string.Empty;
                ws.Cell(r, c++).Value = row.GUARANTEE_TYPE ?? string.Empty;
                ws.Cell(r, c++).Value = row.GUARANTEE_STATUS ?? string.Empty;
                ws.Cell(r, c++).Value = row.Action.HasValue ? EnumHelper.GetActionName(row.Action.Value, userFields) : string.Empty;
                ws.Cell(r, c++).Value = row.ActionStatus.HasValue ? (row.ActionStatus.Value ? "DONE" : "PENDING") : string.Empty;
                SetDate(ws.Cell(r, c++), row.ActionDate);
                ws.Cell(r, c++).Value = row.KPI.HasValue ? EnumHelper.GetKPIName(row.KPI.Value, userFields) : string.Empty;
                ws.Cell(r, c++).Value = row.IncidentType.HasValue ? EnumHelper.GetIncidentName(row.IncidentType.Value, userFields) : string.Empty;
                ws.Cell(r, c++).Value = row.RiskyItem ? "Yes" : string.Empty;
                ws.Cell(r, c++).Value = row.ReasonNonRisky.HasValue ? ResolveUserFieldLabel(row.ReasonNonRisky.Value, userFields, "RISKY") : string.Empty;
                ws.Cell(r, c++).Value = row.IncNumber ?? string.Empty;
                SetDate(ws.Cell(r, c++), row.FirstClaimDate);
                SetDate(ws.Cell(r, c++), row.LastClaimDate);
                ws.Cell(r, c++).Value = row.ToRemind ? "Yes" : string.Empty;
                SetDate(ws.Cell(r, c++), row.ToRemindDate);
                SetDate(ws.Cell(r, c++), row.TriggerDate);
                ws.Cell(r, c++).Value = row.Assignee ?? string.Empty;
                ws.Cell(r, c++).Value = row.LastComment ?? string.Empty;
                ws.Cell(r, c++).Value = row.MbawData ?? string.Empty;
                SetDate(ws.Cell(r, c++), row.Reco_CreationDate);
                SetDate(ws.Cell(r, c++), row.Reco_LastModified);
                ws.Cell(r, c++).Value = row.Reco_ModifiedBy ?? string.Empty;
                ws.Cell(r, c++).Value = row.IsToReview ? "Yes" : string.Empty;
                ws.Cell(r, c++).Value = row.IsReviewed ? "Yes" : string.Empty;
                r++;
            }

            // Freeze header + enable autofilter over the full data range.
            ws.SheetView.FreezeRows(1);
            if (rows.Count > 0)
            {
                ws.Range(1, 1, rows.Count + 1, headers.Length).SetAutoFilter();
            }
            // Auto-size a reasonable subset of columns — adjusting ALL columns on 10k rows is slow.
            try { ws.Columns(1, Math.Min(headers.Length, 16)).AdjustToContents(); } catch { }
        }

        private static void SetDate(IXLCell cell, DateTime? value)
        {
            if (!value.HasValue) return;
            cell.Value = value.Value;
            cell.Style.DateFormat.Format = "yyyy-mm-dd";
        }

        private static void SetDecimal(IXLCell cell, decimal value)
        {
            cell.Value = (double)value;
            cell.Style.NumberFormat.Format = "#,##0.00";
        }

        private static string ResolveUserFieldLabel(int id, IEnumerable<UserField> userFields, string category)
        {
            if (userFields == null) return id.ToString(CultureInfo.InvariantCulture);
            var uf = userFields.FirstOrDefault(u => u.USR_ID == id
                && string.Equals(u.USR_Category, category, StringComparison.OrdinalIgnoreCase));
            if (uf == null) return id.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(uf.USR_FieldName)) return uf.USR_FieldName;
            if (!string.IsNullOrWhiteSpace(uf.USR_FieldDescription)) return uf.USR_FieldDescription;
            return id.ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region Helpers

        private static string FormatAmount(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

        private Country ResolveCountry(string countryId)
        {
            if (_offlineFirstService?.CurrentCountry != null
                && string.Equals(_offlineFirstService.CurrentCountry.CNT_Id, countryId, StringComparison.OrdinalIgnoreCase))
            {
                return _offlineFirstService.CurrentCountry;
            }
            return null;
        }

        private async Task<DateTime?> TryGetAmbreSnapshotDateAsync(string countryId, CancellationToken ct)
        {
            try
            {
                return await _reconciliationService.GetLastAmbreOperationDateAsync(countryId, ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
