using System;
using System.Collections.Generic;
using System.Linq;
using RecoTool.Models;
using RecoTool.Services.DTOs;

namespace RecoTool.Services
{
    public static class ViewDataEnricher
    {
        private static Dictionary<int, string> _userFieldNameCache;
        private static Dictionary<int, string> _userFieldColorCache;
        private static Dictionary<string, string> _assigneeNameCache;
        private static IReadOnlyList<UserField> _lastUserFields;

        #region Public enrichment entry points
        public static void EnrichAll(
            IEnumerable<ReconciliationViewData> data,
            IReadOnlyList<UserField> userFields,
            IEnumerable<dynamic> assignees = null)
        {
            if (data == null) return;
            RebuildCachesIfNeeded(userFields, assignees);
            foreach (var row in data) EnrichRow(row);
        }

        public static void EnrichRow(ReconciliationViewData row)
        {
            if (row == null) return;
            try
            {
                // These three are already present in your original code
                row.ActionDisplayName = GetUserFieldName(row.Action);
                row.ActionBackgroundColor = GetActionColor(row.Action, row.ActionStatus);
                row.KpiDisplayName = GetUserFieldName(row.KPI);
                row.IncidentTypeDisplayName = GetUserFieldName(row.IncidentType);
                row.AssigneeDisplayName = GetAssigneeName(row.Assignee);
                row.GuaranteeTypeDisplay = GetGuaranteeTypeDisplay(row.G_GUARANTEE_TYPE);
                row.ReasonNonRiskyDisplayName = GetUserFieldName(row.ReasonNonRisky);
            }
            catch { }
        }
        #endregion

        #region Individual refresh helpers (old & new)

        // -----------------------------------------------------------------
        // 1️⃣  Existing Action‑only refresh (kept for backwards compatibility)
        // -----------------------------------------------------------------
        public static void RefreshActionDisplay(ReconciliationViewData row)
        {
            // Forward to the generic method – no behavioural change.
            RefreshUserFieldDisplay(row, "Action");
        }

        // -----------------------------------------------------------------
        // 2️⃣  New dedicated refreshes
        // -----------------------------------------------------------------
        static void RefreshKpiDisplay(ReconciliationViewData row)
        {
            if (row == null) return;
            row.KpiDisplayName = GetUserFieldName(row.KPI);
            // If you expose a background‑color property for KPI, update it here:
            // row.KpiBackgroundColor = GetKpiColor(row.KPI);
        }

        public static void RefreshIncidentDisplay(ReconciliationViewData row)
        {
            if (row == null) return;
            row.IncidentTypeDisplayName = GetUserFieldName(row.IncidentType);
            // row.IncidentBackgroundColor = GetIncidentColor(row.IncidentType); // optional
        }

        public static void RefreshReasonNonRiskyDisplay(ReconciliationViewData row)
        {
            if (row == null) return;
            row.ReasonNonRiskyDisplayName = GetUserFieldName(row.ReasonNonRisky);
        }

        // -----------------------------------------------------------------
        // 3️⃣  Generic method that picks the right refresh routine
        // -----------------------------------------------------------------
        /// <summary>
        /// Refreshes the UI‑only properties of <paramref name="row"/> according
        /// to the supplied <paramref name="category"/>. Accepted values (case‑insensitive):
        /// "Action", "KPI", "Incident Type", "ReasonNonRisky".
        /// </summary>
        public static void RefreshUserFieldDisplay(ReconciliationViewData row, string category)
        {
            if (row == null || string.IsNullOrWhiteSpace(category)) return;

            switch (category.Trim().ToUpperInvariant())
            {
                case "ACTION":
                    row.ActionDisplayName = GetUserFieldName(row.Action);
                    row.ActionBackgroundColor = GetActionColor(row.Action, row.ActionStatus);
                    break;

                case "KPI":
                    row.KpiDisplayName = GetUserFieldName(row.KPI);
                    // row.KpiBackgroundColor = GetKpiColor(row.KPI); // optional
                    break;

                case "INCIDENT TYPE":
                case "INCIDENT":
                    row.IncidentTypeDisplayName = GetUserFieldName(row.IncidentType);
                    // row.IncidentBackgroundColor = GetIncidentColor(row.IncidentType); // optional
                    break;

                case "REASONNONRISKY":
                case "REASON NON RISKY":
                case "REASON NON‑RISKY":
                case "REASON_NON_RISKY":
                    row.ReasonNonRiskyDisplayName = GetUserFieldName(row.ReasonNonRisky);
                    break;

                default:
                    // Unknown category – silently ignore.
                    break;
            }
        }
        #endregion

        #region Helper used by QuickSetUserFieldMenuItem_Click (single point of update)

        /// <summary>
        /// Sets the requested user‑field (Action / KPI / Incident Type / ReasonNonRisky)
        /// on both the DTO (<paramref name="row"/>) and the persistence entity
        /// (<paramref name="reco"/>), then refreshes the UI representation.
        /// </summary>
        /// <param name="row">Row currently displayed in the grid.</param>
        /// <param name="reco">Corresponding Reconciliation entity.</param>
        /// <param name="newId">Id of the user‑field to apply (null = clear).</param>
        /// <param name="category">
        /// Exact category string as used in the context‑menu tags:
        /// "Action", "KPI", "Incident Type", "ReasonNonRisky".
        /// </param>
        /// <param name="allUserFields">Full list of UserField objects (required for Action).</param>
        public static void ApplyUserFieldAndRefresh(
            ReconciliationViewData row,
            Reconciliation reco,
            int? newId,
            string category,
            IReadOnlyList<UserField> allUserFields)
        {
            if (row == null || reco == null || string.IsNullOrWhiteSpace(category)) return;

            switch (category.Trim().ToUpperInvariant())
            {
                case "ACTION":
                    // Action needs the full user‑field list for validation.
                    UserFieldUpdateService.ApplyAction(row, reco, newId, allUserFields);
                    RefreshActionDisplay(row);
                    break;

                case "KPI":
                    UserFieldUpdateService.ApplyKpi(row, reco, newId);
                    RefreshKpiDisplay(row);
                    break;

                case "INCIDENT TYPE":
                case "INCIDENT":
                    UserFieldUpdateService.ApplyIncidentType(row, reco, newId);
                    RefreshIncidentDisplay(row);
                    break;

                case "REASONNONRISKY":
                case "REASON NON RISKY":
                case "REASON NON‑RISKY":
                case "REASON_NON_RISKY":
                    // No dedicated service – just assign the value.
                    row.ReasonNonRisky = newId;
                    reco.ReasonNonRisky = newId;
                    RefreshReasonNonRiskyDisplay(row);
                    break;

                default:
                    // If you add more categories later, just extend the switch.
                    break;
            }
        }
        #endregion

        #region Cache handling (unchanged)
        private static void RebuildCachesIfNeeded(IReadOnlyList<UserField> userFields, IEnumerable<dynamic> assignees)
        {
            if (!ReferenceEquals(userFields, _lastUserFields) && userFields != null)
            {
                _userFieldNameCache = new Dictionary<int, string>();
                _userFieldColorCache = new Dictionary<int, string>();
                foreach (var uf in userFields)
                {
                    if (!_userFieldNameCache.ContainsKey(uf.USR_ID))
                    {
                        _userFieldNameCache[uf.USR_ID] = uf.USR_FieldName ?? string.Empty;
                        _userFieldColorCache[uf.USR_ID] = NormalizeColor(uf.USR_Color);
                    }
                }
                _lastUserFields = userFields;
            }

            if (assignees != null)
            {
                _assigneeNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in assignees)
                {
                    try
                    {
                        var id = a?.Id?.ToString();
                        var name = a?.Name?.ToString();
                        if (!string.IsNullOrEmpty(id) && !_assigneeNameCache.ContainsKey(id))
                            _assigneeNameCache[id] = name ?? string.Empty;
                    }
                    catch { }
                }
            }
        }

        public static void InvalidateCaches()
        {
            _userFieldNameCache = null;
            _userFieldColorCache = null;
            _assigneeNameCache = null;
            _lastUserFields = null;
        }
        #endregion

        #region Cache lookup helpers (unchanged)
        private static string GetUserFieldName(int? id)
        {
            if (!id.HasValue || _userFieldNameCache == null) return string.Empty;
            return _userFieldNameCache.TryGetValue(id.Value, out var name) ? name : string.Empty;
        }

        private static string GetActionColor(int? actionId, bool? actionStatus)
        {
            if (actionStatus == true) return "Transparent";
            if (!actionId.HasValue || _userFieldColorCache == null) return "Transparent";
            return _userFieldColorCache.TryGetValue(actionId.Value, out var color) ? color : "Transparent";
        }

        // -----------------------------------------------------------------
        // OPTIONAL: colour helpers for KPI / Incident / ReasonNonRisky.
        // If you have a colour column in the DB, replace the stub below
        // with the real logic (or simply return "Transparent").
        // -----------------------------------------------------------------
        private static string GetKpiColor(int? kpiId) => "Transparent";
        private static string GetIncidentColor(int? incidentId) => "Transparent";
        private static string GetReasonNonRiskyColor(int? reasonId) => "Transparent";

        private static string NormalizeColor(string colorRaw)
        {
            var color = colorRaw?.Trim()?.ToUpperInvariant();
            if (string.IsNullOrEmpty(color)) return "Transparent";
            switch (color)
            {
                case "RED": return "#FFCDD2";
                case "GREEN": return "#C8E6C9";
                case "YELLOW": return "#FFF9C4";
                case "BLUE": return "#BBDEFB";
                default: return color.StartsWith("#") ? color : "Transparent";
            }
        }

        private static string GetAssigneeName(string assigneeId)
        {
            if (string.IsNullOrEmpty(assigneeId) || _assigneeNameCache == null) return string.Empty;
            return _assigneeNameCache.TryGetValue(assigneeId, out var name) ? name : string.Empty;
        }

        private static string GetGuaranteeTypeDisplay(string guaranteeType)
        {
            var s = guaranteeType?.Trim();
            if (string.IsNullOrEmpty(s)) return string.Empty;
            switch (s.ToUpperInvariant())
            {
                case "REISSU": return "REISSUANCE";
                case "ISSU": return "ISSUANCE";
                case "NOTIF": return "ADVISING";
                default: return s;
            }
        }
        #endregion
    }
}