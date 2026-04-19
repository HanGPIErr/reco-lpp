using OfflineFirstAccess.Helpers;
using RecoTool.Infrastructure.Logging;
using RecoTool.Models;
using RecoTool.Services.DTOs;
using RecoTool.Services.Helpers;
using RecoTool.Services.Rules;
using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Linq;
using System.Threading.Tasks;

namespace RecoTool.Services.Ambre
{
    /// <summary>
    /// Rule evaluation and application for <see cref="AmbreReconciliationUpdater"/>:
    /// builds <see cref="RuleContext"/> per staged row, evaluates truth-table rules,
    /// propagates SELF/COUNTERPART outputs, applies fallback/hook rules
    /// (<c>DIRECT_DEBIT → COLLECTION</c>, <c>IT Issue → INVESTIGATE</c>, <c>ReasonNonRisky</c>
    /// auto-set, global <c>INVESTIGATE</c> fallback) and re-applies rules to existing rows.
    /// </summary>
    public partial class AmbreReconciliationUpdater
    {
        private RuleContext BuildRuleContext(DataAmbre dataAmbre, Reconciliation reconciliation, Country country, string countryId, bool isPivot, IReadOnlyList<DwingsInvoiceDto> dwInvoices, IReadOnlyList<DwingsGuaranteeDto> dwGuarantees, bool isGrouped, decimal? missingAmount, bool isNewLine)
        {
            // Determine transaction type enum name
            TransactionType? tx;
            
            if (isPivot)
            {
                // For PIVOT: use Category field (enum TransactionType)
                tx = _transformationService.DetermineTransactionType(dataAmbre.RawLabel, isPivot, dataAmbre.Category);
            }
            else
            {
                // For RECEIVABLE: use PAYMENT_METHOD from DWINGS invoice if available
                string paymentMethod = null;
                if (!string.IsNullOrWhiteSpace(reconciliation?.DWINGS_InvoiceID))
                {
                    DwingsInvoiceDto inv;
                    if (_invoiceById != null && _invoiceById.TryGetValue(reconciliation.DWINGS_InvoiceID, out inv))
                        paymentMethod = inv?.PAYMENT_METHOD;
                }
                
                // Map PAYMENT_METHOD to TransactionType enum
                if (!string.IsNullOrWhiteSpace(paymentMethod))
                {
                    var upperMethod = paymentMethod.Trim().ToUpperInvariant().Replace(' ', '_');
                    if (Enum.TryParse<TransactionType>(upperMethod, true, out var parsed))
                    {
                        tx = parsed;
                    }
                    else
                    {
                        // Fallback to label-based detection
                        tx = _transformationService.DetermineTransactionType(dataAmbre.RawLabel, isPivot, null);
                    }
                }
                else
                {
                    // No PAYMENT_METHOD available, use label-based detection
                    tx = _transformationService.DetermineTransactionType(dataAmbre.RawLabel, isPivot, null);
                }
            }
            
            string txName = tx.HasValue ? Enum.GetName(typeof(TransactionType), tx.Value) : null;

            // Guarantee type from DWINGS (requires a DWINGS_GuaranteeID link)
            string guaranteeType = null;
            if (!isPivot && !string.IsNullOrWhiteSpace(reconciliation?.DWINGS_GuaranteeID))
            {
                try
                {
                    DwingsGuaranteeDto guar;
                    if (_guaranteeById != null && _guaranteeById.TryGetValue(reconciliation.DWINGS_GuaranteeID, out guar))
                        guaranteeType = guar?.GUARANTEE_TYPE;
                }
                catch { }
            }

            // Sign from amount
            var sign = dataAmbre.SignedAmount >= 0 ? "C" : "D";

            // Presence of DWINGS links (any of Invoice/Guarantee/BGPMT)
            bool? hasDw = (!string.IsNullOrWhiteSpace(reconciliation?.DWINGS_InvoiceID)
                          || !string.IsNullOrWhiteSpace(reconciliation?.DWINGS_GuaranteeID)
                          || !string.IsNullOrWhiteSpace(reconciliation?.DWINGS_BGPMT));

            // Extended time/state inputs
            var today = DateTime.Today;
            
            // FIXED: Nullable boolean logic - only set to bool value if we can determine it, otherwise keep null
            bool? triggerDateIsNull = reconciliation?.TriggerDate.HasValue == true ? (bool?)false : (reconciliation != null ? (bool?)true : null);
            
            int? daysSinceTrigger = reconciliation?.TriggerDate.HasValue == true
                ? (int?)(today - reconciliation.TriggerDate.Value.Date).TotalDays
                : null;
            
            int? operationDaysAgo = dataAmbre.Operation_Date.HasValue
                ? (int?)(today - dataAmbre.Operation_Date.Value.Date).TotalDays
                : null;
            
            bool? isMatched = hasDw; // consider matched when any DWINGS link is present
            bool? hasManualMatch = null; // unknown at import time
            
            // FIXED: IsFirstRequest should be null if we don't have reconciliation data
            bool? isFirstRequest = reconciliation?.FirstClaimDate.HasValue == true ? (bool?)false : (reconciliation != null ? (bool?)true : null);
            
            int? daysSinceReminder = reconciliation?.LastClaimDate.HasValue == true
                ? (int?)(today - reconciliation.LastClaimDate.Value.Date).TotalDays
                : null;

            // OPTIMIZATION: Use pre-built dictionary instead of O(n) linear scan
            string mtStatus = null;
            bool? hasCommEmail = null;
            bool? bgiInitiated = null;
            string invoiceStatus = null;
            string paymentRequestStatus = null;
            try
            {
                DwingsInvoiceDto invLookup;
                if (!string.IsNullOrWhiteSpace(reconciliation?.DWINGS_InvoiceID)
                    && _invoiceById != null && _invoiceById.TryGetValue(reconciliation.DWINGS_InvoiceID, out invLookup)
                    && invLookup != null)
                {
                    mtStatus = invLookup.MT_STATUS;
                    hasCommEmail = invLookup.COMM_ID_EMAIL;
                    invoiceStatus = invLookup.T_INVOICE_STATUS;
                    paymentRequestStatus = invLookup.T_PAYMENT_REQUEST_STATUS;
                    if (!string.IsNullOrWhiteSpace(invLookup.T_INVOICE_STATUS))
                        bgiInitiated = string.Equals(invLookup.T_INVOICE_STATUS, "INITIATED", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }

            return new RuleContext
            {
                CountryId = countryId,
                IsPivot = isPivot,
                GuaranteeType = guaranteeType,
                TransactionType = txName,
                HasDwingsLink = hasDw,
                IsGrouped = isGrouped,
                IsAmountMatch = isGrouped && missingAmount.HasValue && missingAmount.Value == 0,
                MissingAmount = missingAmount,
                Sign = sign,
                Bgi = reconciliation?.DWINGS_InvoiceID,
                // Extended fields
                TriggerDateIsNull = triggerDateIsNull,
                DaysSinceTrigger = daysSinceTrigger,
                OperationDaysAgo = operationDaysAgo,
                IsMatched = isMatched,
                HasManualMatch = hasManualMatch,
                IsFirstRequest = isFirstRequest,
                IsNewLine = isNewLine,
                DaysSinceReminder = daysSinceReminder,
                CurrentActionId = reconciliation?.Action,
                // FIX: was missing — rules with IsActionDone condition never fired during import
                IsActionDone = reconciliation?.ActionStatus,
                // New DWINGS-derived
                MtStatus = mtStatus,
                HasCommIdEmail = hasCommEmail,
                IsBgiInitiated = bgiInitiated,
                // FIX: were never populated — dead conditions for rules like R-DIRDEB-FULLY-EXECUTED
                InvoiceStatus = invoiceStatus,
                PaymentRequestStatus = paymentRequestStatus
            };
        }

        /// <summary>
        /// HARD-CODED RULE: For PIVOT lines with DIRECT_DEBIT payment method, set Category to COLLECTION
        /// This must run BEFORE truth-table rules
        /// </summary>
        private void ApplyDirectDebitCollectionRule(List<ReconciliationStaging> staged, IReadOnlyList<DwingsInvoiceDto> dwInvoices)
        {
            if (staged == null || dwInvoices == null) return;

            int appliedCount = 0;
            foreach (var s in staged)
            {
                // Only for PIVOT lines
                if (!s.IsPivot) continue;

                // Check if line has BGI or BGPMT
                var bgi = s.Reconciliation?.DWINGS_InvoiceID;
                var bgpmt = s.Reconciliation?.DWINGS_BGPMT;
                
                if (string.IsNullOrWhiteSpace(bgi) && string.IsNullOrWhiteSpace(bgpmt))
                    continue;

                // Find invoice and check payment method (O(1) dictionary lookup)
                DwingsInvoiceDto invoice = null;
                if (_invoiceById != null && !string.IsNullOrWhiteSpace(bgi))
                    _invoiceById.TryGetValue(bgi, out invoice);
                if (invoice == null && _invoiceByBgpmt != null && !string.IsNullOrWhiteSpace(bgpmt))
                    _invoiceByBgpmt.TryGetValue(bgpmt, out invoice);

                if (invoice != null && string.Equals(invoice.PAYMENT_METHOD, "DIRECT_DEBIT", StringComparison.OrdinalIgnoreCase))
                {
                    // Set Category to COLLECTION (enum value = 0)
                    s.DataAmbre.Category = (int)TransactionType.COLLECTION;
                    appliedCount++;
                }
            }

            if (appliedCount > 0)
                LogManager.Info($"[HARD-CODED RULE] DIRECT_DEBIT → COLLECTION: Applied to {appliedCount} PIVOT line(s)");
        }

        private async Task ApplyTruthTableRulesAsync(
                List<ReconciliationStaging> staged,
                Country country,
                string countryId,
                IReadOnlyList<DwingsInvoiceDto> dwInvoices,
                IReadOnlyList<DwingsGuaranteeDto> dwGuarantees,
                bool isNewLines = true)
        {
            try
            {
                if (staged == null || staged.Count == 0) return;

                /* --------------------------------------------------------------
                 * 1️⃣  Première passe – exécution des règles (SELF) + collecte du
                 *     COUNTERPART (par BGI)
                 * -------------------------------------------------------------- */
                var counterpartIntents = new List<(string Bgi,
                                                  bool TargetIsPivot,
                                                  string RuleId,
                                                  int? ActionId,
                                                  int? KpiId,
                                                  int? IncidentTypeId,
                                                  bool? RiskyItem,
                                                  int? ReasonNonRiskyId,
                                                  bool? ToRemind,
                                                  int? ToRemindDays,
                                                  bool? ActionDone,
                                                  bool? FirstClaimToday)>();

                const int batchSize = 2000;                     // 2000 lignes par lot
                var allEvalResults = new List<(ReconciliationStaging Staging,
                                              RuleEvaluationResult Result)>();

                for (int i = 0; i < staged.Count; i += batchSize)
                {
                    var batch = staged.Skip(i).Take(batchSize).ToList();

                    var batchTasks = batch.Select(async s =>
                    {
                        var ctx = BuildRuleContext(
                                       s.DataAmbre,
                                       s.Reconciliation,
                                       country,
                                       countryId,
                                       s.IsPivot,
                                       dwInvoices,
                                       dwGuarantees,
                                       s.IsGrouped,
                                       s.MissingAmount,
                                       isNewLines);

                        var res = await _rulesEngine
                                       .EvaluateAsync(ctx, RuleScope.Import)
                                       .ConfigureAwait(false);
                        return (Staging: s, Result: res);
                    }).ToList();

                    var batchResults = await Task.WhenAll(batchTasks);
                    allEvalResults.AddRange(batchResults);

                    if (i > 0 && i % 10000 == 0)
                        LogManager.Info($"[PERF] Rules evaluation progress: {i}/{staged.Count} records processed");
                }

                /* --------------------------------------------------------------
                 * 2️⃣  Application des sorties SELF
                 * -------------------------------------------------------------- */
                foreach (var tuple in allEvalResults)
                {
                    var s = tuple.Staging;
                    var res = tuple.Result;

                    if (res == null || res.Rule == null) continue;

                    /* ----- ne pas écraser les lignes déjà traitées par MANUAL_OUTGOING ----- */
                    //if (s.Reconciliation.Action.HasValue && s.Reconciliation.KPI.HasValue)
                    //    continue;

                    /* ------------------- SELF ------------------- */
                    if (res.Rule.ApplyTo == ApplyTarget.Self || res.Rule.ApplyTo == ApplyTarget.Both)
                    {
                        RuleApplicationHelper.ApplyOutputs(res, s.Reconciliation, _currentUser);
                        try
                        {
                            var summary = RuleApplicationHelper.BuildOutputSummary(res);
                            LogHelper.WriteRuleApplied("import", countryId, s.Reconciliation?.ID, res.Rule.RuleId, summary, res.UserMessage);
                        }
                        catch { }
                    }

                    /* ------------------- COUNTERPART ------------------- */
                    if ((res.Rule.ApplyTo == ApplyTarget.Counterpart || res.Rule.ApplyTo == ApplyTarget.Both)
                        && !string.IsNullOrWhiteSpace(s.Bgi))
                    {
                        var targetIsPivot = !s.IsPivot;   // le côté opposé
                        counterpartIntents.Add((
                            Bgi: s.Bgi.Trim().ToUpperInvariant(),
                            TargetIsPivot: targetIsPivot,
                            RuleId: res.Rule.RuleId,
                            ActionId: res.Rule.OutputActionId,
                            KpiId: res.Rule.OutputKpiId,
                            IncidentTypeId: res.Rule.OutputIncidentTypeId,
                            RiskyItem: res.Rule.OutputRiskyItem,
                            ReasonNonRiskyId: res.Rule.OutputReasonNonRiskyId,
                            ToRemind: res.Rule.OutputToRemind,
                            ToRemindDays: res.Rule.OutputToRemindDays,
                            // FIX: were missing — counterpart line was not receiving ActionStatus/FirstClaim outputs
                            ActionDone: res.Rule.OutputActionDone,
                            FirstClaimToday: res.Rule.OutputFirstClaimToday));
                    }
                }

                /* --------------------------------------------------------------
                 * 3️⃣  Deuxième passe – application des intentions COUNTERPART
                 * -------------------------------------------------------------- */
                if (counterpartIntents.Count > 0)
                {
                    var byBgi = staged
                        .Where(x => !string.IsNullOrWhiteSpace(x.Bgi))
                        .GroupBy(x => x.Bgi.Trim().ToUpperInvariant())
                        .ToDictionary(g => g.Key, g => g.ToList());

                    foreach (var intent in counterpartIntents)
                    {
                        if (!byBgi.TryGetValue(intent.Bgi, out var rows)) continue;

                        foreach (var row in rows)
                        {
                            if (row.IsPivot != intent.TargetIsPivot) continue;

                            // Idempotent counterpart application: only mutate fields when value differs.
                            bool changed = false;
                            var reco = row.Reconciliation;

                            if (intent.ActionId.HasValue && reco.Action != intent.ActionId.Value)
                            { reco.Action = intent.ActionId.Value; changed = true; }

                            if (intent.KpiId.HasValue && reco.KPI != intent.KpiId.Value)
                            { reco.KPI = intent.KpiId.Value; changed = true; }

                            if (intent.IncidentTypeId.HasValue && reco.IncidentType != intent.IncidentTypeId.Value)
                            { reco.IncidentType = intent.IncidentTypeId.Value; changed = true; }

                            if (intent.RiskyItem.HasValue && reco.RiskyItem != intent.RiskyItem.Value)
                            { reco.RiskyItem = intent.RiskyItem.Value; changed = true; }

                            if (intent.ReasonNonRiskyId.HasValue && reco.ReasonNonRisky != intent.ReasonNonRiskyId.Value)
                            { reco.ReasonNonRisky = intent.ReasonNonRiskyId.Value; changed = true; }

                            if (intent.ToRemind.HasValue && reco.ToRemind != intent.ToRemind.Value)
                            { reco.ToRemind = intent.ToRemind.Value; changed = true; }

                            if (intent.ToRemindDays.HasValue)
                            {
                                try
                                {
                                    var target = DateTime.Today.AddDays(intent.ToRemindDays.Value);
                                    if (reco.ToRemindDate != target) { reco.ToRemindDate = target; changed = true; }
                                }
                                catch { }
                            }

                            // Propagate OutputActionDone to counterpart (stamps ActionStatus + ActionDate only when status changes)
                            if (intent.ActionDone.HasValue && reco.ActionStatus != intent.ActionDone.Value)
                            {
                                reco.ActionStatus = intent.ActionDone.Value;
                                try { reco.ActionDate = DateTime.Now; } catch { }
                                changed = true;
                            }

                            // Propagate OutputFirstClaimToday to counterpart (idempotent)
                            if (intent.FirstClaimToday == true)
                            {
                                var today = DateTime.Today;
                                if (reco.FirstClaimDate.HasValue)
                                {
                                    if (reco.LastClaimDate != today) { reco.LastClaimDate = today; changed = true; }
                                }
                                else
                                {
                                    reco.FirstClaimDate = today; changed = true;
                                }
                            }

                            // Skip logging if nothing actually changed (avoid log spam on re-imports)
                            if (!changed) continue;

                            // Log counterpart (facultatif)
                            try
                            {
                                var parts = new List<string>();
                                if (intent.ActionId.HasValue) parts.Add($"Action={intent.ActionId.Value}");
                                if (intent.KpiId.HasValue) parts.Add($"KPI={intent.KpiId.Value}");
                                if (intent.IncidentTypeId.HasValue) parts.Add($"IncidentType={intent.IncidentTypeId.Value}");
                                if (intent.RiskyItem.HasValue) parts.Add($"RiskyItem={intent.RiskyItem.Value}");
                                if (intent.ReasonNonRiskyId.HasValue) parts.Add($"ReasonNonRisky={intent.ReasonNonRiskyId.Value}");
                                if (intent.ToRemind.HasValue) parts.Add($"ToRemind={intent.ToRemind.Value}");
                                if (intent.ToRemindDays.HasValue) parts.Add($"ToRemindDays={intent.ToRemindDays.Value}");
                                if (intent.ActionDone.HasValue) parts.Add($"ActionStatus={(intent.ActionDone.Value ? "DONE" : "PENDING")}");
                                if (intent.FirstClaimToday == true) parts.Add("FirstClaimDate=Today");

                                var outStr = string.Join("; ", parts);
                                LogHelper.WriteRuleApplied("import", countryId, row.Reconciliation?.ID,
                                                           intent.RuleId, outStr, "Counterpart application");
                            }
                            catch { }
                        }
                    }
                }

                /* --------------------------------------------------------------
                 * 4️⃣  Fallback – aucune règle n’a touché la ligne (seulement pour
                 *      les nouvelles lignes)
                 * -------------------------------------------------------------- */
                if (isNewLines)
                {
                    const int ACTION_INVESTIGATE = (int)ActionType.Investigate;   // 7
                    int fallbackCount = 0;

                    foreach (var tuple in allEvalResults)
                    {
                        var s = tuple.Staging;
                        var res = tuple.Result;

                        // Si une action a déjà été mise (MANUAL_OUTGOING, fallback, etc.) on ne touche pas
                        if (s.Reconciliation.Action.HasValue) continue;

                        // Aucun résultat de règle → appliquer le fallback
                        if (res == null || res.Rule == null)
                        {
                            s.Reconciliation.Action = ACTION_INVESTIGATE;
                            s.Reconciliation.ActionStatus = true;          // DONE
                            s.Reconciliation.ActionDate = DateTime.Now;

                            // commentaire d’audit
                            var prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {_currentUser}: ";
                            var msg = $"{prefix}New line set to INVESTIGATE – no matching rule found";
                            if (string.IsNullOrWhiteSpace(s.Reconciliation.Comments))
                                s.Reconciliation.Comments = msg;
                            else if (!s.Reconciliation.Comments.Contains("no matching rule found"))
                                s.Reconciliation.Comments = msg + Environment.NewLine + s.Reconciliation.Comments;

                            fallbackCount++;

                            // log fallback
                            try
                            {
                                LogHelper.WriteRuleApplied("import", countryId, s.Reconciliation?.ID,
                                                           "FALLBACK_INVESTIGATE",
                                                           $"Action={ACTION_INVESTIGATE}",
                                                           "No matching rule – default to INVESTIGATE");
                            }
                            catch { }
                        }
                    }

                    if (fallbackCount > 0)
                        LogManager.Info($"[FALLBACK RULE] Applied INVESTIGATE to {fallbackCount} line(s) (no matching rule).");
                }

                /* --------------------------------------------------------------
                 * 5️⃣ Post‑processing – ReasonNonRisky & IT‑Issue → INVESTIGATE
                 * -------------------------------------------------------------- */
                const int REASON_NOT_OBSERVED = (int)Risky.NoObservedRiskExpectedDelay;   // 32
                const int REASON_COMMISSION_PAID = (int)Risky.CollectedCommissionsCredit67P; // 30
                const int ACTION_INVEST = (int)ActionType.Investigate;               // 7
                const int KPI_IT_ISSUES = (int)KPIType.ITIssues;                     // 19

                foreach (var s in staged)
                {
                    var rec = s.Reconciliation;
                    if (rec == null) continue;

                    /* ----- ReasonNonRisky automatique ----- */
                    if (rec.ActionStatus == true && !rec.ReasonNonRisky.HasValue)
                    {
                        // heuristique : si la ligne possède déjà un TriggerDate, on considère que c’est le système qui a déjà déclenché
                        bool systemTrigger = rec.TriggerDate.HasValue && rec.TriggerDate.Value < DateTime.Today;
                        rec.ReasonNonRisky = systemTrigger ? REASON_COMMISSION_PAID : REASON_NOT_OBSERVED;
                    }

                    /* ----- IT Issue → FORCE INVESTIGATE (DONE) — idempotent ----- */
                    if (rec.KPI.HasValue && rec.KPI.Value == KPI_IT_ISSUES)
                    {
                        bool changed = false;
                        if (rec.Action != ACTION_INVEST) { rec.Action = ACTION_INVEST; changed = true; }
                        if (rec.ActionStatus != true) { rec.ActionStatus = true; changed = true; }
                        if (changed) rec.ActionDate = DateTime.Now;
                    }
                }

                /* --------------------------------------------------------------
                 * 6️⃣ Normalisation finale de ActionStatus / ActionDate
                 * -------------------------------------------------------------- */
                try
                {
                    var allUserFields = _offlineFirstService?.UserFields;
                    var nowLocal = DateTime.Now;

                    foreach (var s in staged)
                    {
                        var rec = s?.Reconciliation;
                        if (rec == null) continue;

                        // Si ActionStatus a déjà été fixé (par la règle ou le fallback) on le garde,
                        // sinon on applique la règle « NA = DONE, sinon PENDING »
                        bool statusJustSet = false;
                        if (!rec.ActionStatus.HasValue)
                        {
                            bool isNa = !rec.Action.HasValue || UserFieldUpdateService.IsActionNA(rec.Action, allUserFields);
                            rec.ActionStatus = isNa;               // true = DONE, false = PENDING
                            statusJustSet = true;
                        }

                        // Stamp ActionDate only when missing or when status was just set.
                        // Avoids overwriting user's / previous import's timestamp on each re-import (was a source of spurious diffs).
                        if ((rec.Action.HasValue || rec.ActionStatus.HasValue) && (statusJustSet || !rec.ActionDate.HasValue))
                        {
                            rec.ActionDate = nowLocal;
                        }
                    }
                }
                catch { /* non‑bloquant */ }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Truth‑table rules application failed: {ex.Message}");
            }
        }

        /// -------------------------------------------------------------------
        /// 1️⃣  Si une action a été marquée DONE → assigner automatiquement
        ///     le ReasonNonRisky adéquat
        /// -------------------------------------------------------------------
        private void AutoSetReasonNonRisky(List<ReconciliationStaging> staged)
        {
            const int REASON_WE_DO_NOT_OBSERVE = 32; // “We do not observe risk …”
            const int REASON_COMMISSION_PAID = 30; // “Commissions already collected …”

            foreach (var s in staged)
            {
                var rec = s.Reconciliation;
                if (!rec.ActionStatus.HasValue) continue; // aucune action appliquée

                // 1️⃣ User‑triggered (ActionStatus = true && ReasonNonRisky is null)
                if (!rec.ReasonNonRisky.HasValue)
                {
                    // le trigger vient d’être appliqué automatiquement ? → on regarde le trigger date
                    bool systemTrigger = rec.TriggerDate.HasValue && rec.TriggerDate.Value < DateTime.Today;
                    rec.ReasonNonRisky = systemTrigger ? REASON_COMMISSION_PAID : REASON_WE_DO_NOT_OBSERVE;
                }
            }
        }

        /// -------------------------------------------------------------------
        /// 2️⃣  KPI = IT ISSUE ⇒ forcer Action = INVESTIGATE (DONE)
        /// -------------------------------------------------------------------
        private void EnforceItIssueAction(List<ReconciliationStaging> staged)
        {
            const int KPI_IT_ISSUE = (int)KPIType.ITIssues;   // 19
            const int ACTION_INVEST = (int)ActionType.Investigate; // 7

            foreach (var s in staged)
            {
                if (s.Reconciliation?.KPI == KPI_IT_ISSUE)
                {
                    s.Reconciliation.Action = ACTION_INVEST;
                    s.Reconciliation.ActionStatus = true;                // DONE
                }
            }
        }

        /// -------------------------------------------------------------------
        /// 3️⃣  Fallback : aucune règle n’a matché → action INVESTIGATE (DONE)
        /// -------------------------------------------------------------------
        private void ApplyFallbackRule(List<ReconciliationStaging> staged)
        {
            const int ACTION_INVEST = (int)ActionType.Investigate; // 7

            foreach (var s in staged)
            {
                // Si aucune colonne d’action ou de KPI n’a été remplie par le moteur…
                if (!s.Reconciliation.Action.HasValue && !s.Reconciliation.KPI.HasValue)
                {
                    s.Reconciliation.Action = ACTION_INVEST;
                    s.Reconciliation.ActionStatus = true;   // DONE
                }
            }
        }

        /// <summary>
        /// Réapplique les règles de truth-table aux enregistrements existants (ToUpdate).
        /// Cela permet de mettre à jour Action, KPI, IncidentType, etc. selon les règles actuelles.
        /// </summary>
        private async Task ApplyRulesToExistingRecordsAsync(
            List<DataAmbre> updatedRecords,
            Country country,
            string countryId,
            IReadOnlyList<DwingsInvoiceDto> dwInvoices,
            IReadOnlyList<DwingsGuaranteeDto> dwGuarantees)
        {
            try
            {
                if (updatedRecords == null || updatedRecords.Count == 0) return;

                var timer = System.Diagnostics.Stopwatch.StartNew();
                LogManager.Info($"[PERF] Applying truth-table rules to {updatedRecords.Count} existing records");

                // DWINGS data passed from caller to avoid reloading
                _transformationService = new TransformationService(new List<Country> { country });

                // Load existing reconciliations from database
                var connectionString = _offlineFirstService.GetCountryConnectionString(countryId);
                var reconciliations = new Dictionary<string, Reconciliation>(StringComparer.OrdinalIgnoreCase);
                
                using (var conn = new OleDbConnection(connectionString))
                {
                    await conn.OpenAsync();
                    
                    // Batch load reconciliations (Access IN clause limit ~1000)
                    var ids = updatedRecords.Select(r => r.ID).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    const int batchSize = 500;
                    
                    for (int i = 0; i < ids.Count; i += batchSize)
                    {
                        var batch = ids.Skip(i).Take(batchSize).ToList();
                        var inClause = string.Join(",", batch.Select((_, idx) => "?"));
                        
                        using (var cmd = new OleDbCommand(
                            $"SELECT * FROM [T_Reconciliation] WHERE [ID] IN ({inClause})", conn))
                        {
                            foreach (var id in batch)
                                cmd.Parameters.AddWithValue("@ID", id);
                            
                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync())
                                {
                                    var rec = MapReconciliationFromReader(reader);
                                    if (rec != null && !string.IsNullOrWhiteSpace(rec.ID))
                                        reconciliations[rec.ID] = rec;
                                }
                            }
                        }
                    }
                }

                // Create staging items
                var staged = new List<ReconciliationStaging>();
                foreach (var dataAmbre in updatedRecords)
                {
                    if (!reconciliations.TryGetValue(dataAmbre.ID, out var reconciliation))
                        continue;

                    staged.Add(new ReconciliationStaging
                    {
                        Reconciliation = reconciliation,
                        DataAmbre = dataAmbre,
                        IsPivot = dataAmbre.IsPivotAccount(country.CNT_AmbrePivot),
                        Bgi = reconciliation.DWINGS_InvoiceID
                    });
                }

                if (staged.Count == 0)
                {
                    LogManager.Info("No reconciliations found for existing records");
                    return;
                }

                // Calculate KPIs (IsGrouped, MissingAmount)
                var kpiTimer = System.Diagnostics.Stopwatch.StartNew();
                var kpiStaging = staged.Select(s => new ReconciliationKpiCalculator.ReconciliationStaging
                {
                    DataAmbre = s.DataAmbre,
                    Reconciliation = s.Reconciliation,
                    IsPivot = s.IsPivot
                }).ToList();
                
                ReconciliationKpiCalculator.CalculateKpis(kpiStaging);
                
                // Copy calculated KPIs back to staging items
                for (int i = 0; i < staged.Count && i < kpiStaging.Count; i++)
                {
                    staged[i].IsGrouped = kpiStaging[i].IsGrouped;
                    staged[i].MissingAmount = kpiStaging[i].MissingAmount;
                }
                kpiTimer.Stop();
                LogManager.Info($"[PERF] KPI calculation completed for {staged.Count} existing records in {kpiTimer.ElapsedMilliseconds}ms");

                // Apply special MANUAL_OUTGOING pairing rule FIRST (before truth-table rules)
                // This prevents truth-table from overwriting guarantee-based matches
                try
                {
                    var manualOutgoingMatches = await _reconciliationService.ApplyManualOutgoingRuleAsync(countryId).ConfigureAwait(false);
                    if (manualOutgoingMatches > 0)
                    {
                        LogManager.Info($"MANUAL_OUTGOING rule: matched {manualOutgoingMatches} pair(s) - these will be excluded from truth-table rules");
                        
                        // Reload reconciliations that were updated by MANUAL_OUTGOING rule
                        // to ensure staged items have the latest Action/KPI values
                        using (var conn = new OleDbConnection(connectionString))
                        {
                            await conn.OpenAsync();
                            var ids = staged.Select(s => s.Reconciliation.ID).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            const int reloadBatchSize = 500;
                            
                            for (int i = 0; i < ids.Count; i += reloadBatchSize)
                            {
                                var batch = ids.Skip(i).Take(reloadBatchSize).ToList();
                                var inClause = string.Join(",", batch.Select((_, idx) => "?"));
                                
                                using (var cmd = new OleDbCommand(
                                    $"SELECT * FROM [T_Reconciliation] WHERE [ID] IN ({inClause})", conn))
                                {
                                    foreach (var id in batch)
                                        cmd.Parameters.AddWithValue("@ID", id);
                                    
                                    using (var reader = await cmd.ExecuteReaderAsync())
                                    {
                                        while (await reader.ReadAsync())
                                        {
                                            var rec = MapReconciliationFromReader(reader);
                                            if (rec != null && !string.IsNullOrWhiteSpace(rec.ID))
                                            {
                                                // Update staged item with fresh data
                                                var stagedItem = staged.FirstOrDefault(s => string.Equals(s.Reconciliation.ID, rec.ID, StringComparison.OrdinalIgnoreCase));
                                                if (stagedItem != null)
                                                {
                                                    stagedItem.Reconciliation = rec;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Error($"Error applying MANUAL_OUTGOING rule: {ex.Message}", ex);
                }

                // Apply truth-table rules (skip lines already processed by MANUAL_OUTGOING)
                var rulesTimer = System.Diagnostics.Stopwatch.StartNew();
                LogManager.Info($"Evaluating truth-table rules for {staged.Count} existing records...");
                // isNewLines=false disables FALLBACK rule (existing lines should keep their current state if no rule matches)
                await ApplyTruthTableRulesAsync(staged, country, countryId, dwInvoices, dwGuarantees, isNewLines: false);

                // Count how many had rules applied
                rulesTimer.Stop();
                int rulesAppliedCount = staged.Count(s => s.Reconciliation.Action.HasValue || s.Reconciliation.KPI.HasValue);
                LogManager.Info($"[PERF] Rules evaluation complete: {rulesAppliedCount}/{staged.Count} records had rules applied in {rulesTimer.ElapsedMilliseconds}ms");

                // Update database with rule results - OPTIMIZED with batching
                var dbUpdateTimer = System.Diagnostics.Stopwatch.StartNew();
                using (var conn = new OleDbConnection(connectionString))
                {
                    await conn.OpenAsync();
                    using (var tx = conn.BeginTransaction())
                    {
                        try
                        {
                            var nowUtc = DateTime.UtcNow;
                            const int dbBatchSize = 500; // Batch DB updates for better performance
                            int updateCount = 0;
                            
                            using (var cmd = new OleDbCommand(
                                @"UPDATE [T_Reconciliation] SET 
                                    [Action]=?, [KPI]=?, [IncidentType]=?, [RiskyItem]=?, [ReasonNonRisky]=?,
                                    [ToRemind]=?, [ToRemindDate]=?, [FirstClaimDate]=?,
                                    [LastModified]=?, [ModifiedBy]=?
                                  WHERE [ID]=?", conn, tx))
                            {
                                // Pre-create parameters once with explicit sizes for VarChar
                                cmd.Parameters.Add("@Action", OleDbType.Integer);
                                cmd.Parameters.Add("@KPI", OleDbType.Integer);
                                cmd.Parameters.Add("@IncidentType", OleDbType.Integer);
                                cmd.Parameters.Add("@RiskyItem", OleDbType.Boolean);
                                cmd.Parameters.Add("@ReasonNonRisky", OleDbType.Integer);
                                cmd.Parameters.Add("@ToRemind", OleDbType.Boolean);
                                cmd.Parameters.Add("@ToRemindDate", OleDbType.Date);
                                cmd.Parameters.Add("@FirstClaimDate", OleDbType.Date);
                                cmd.Parameters.Add("@LastModified", OleDbType.Date);
                                cmd.Parameters.Add("@ModifiedBy", OleDbType.VarWChar, 255);
                                cmd.Parameters.Add("@ID", OleDbType.VarWChar, 255);
                                
                                foreach (var s in staged)
                                {
                                    var rec = s.Reconciliation;
                                    
                                    cmd.Parameters["@Action"].Value = rec.Action.HasValue ? (object)rec.Action.Value : DBNull.Value;
                                    cmd.Parameters["@KPI"].Value = rec.KPI.HasValue ? (object)rec.KPI.Value : DBNull.Value;
                                    cmd.Parameters["@IncidentType"].Value = rec.IncidentType.HasValue ? (object)rec.IncidentType.Value : DBNull.Value;
                                    cmd.Parameters["@RiskyItem"].Value = rec.RiskyItem.HasValue ? (object)rec.RiskyItem.Value : DBNull.Value;
                                    cmd.Parameters["@ReasonNonRisky"].Value = rec.ReasonNonRisky.HasValue ? (object)rec.ReasonNonRisky.Value : DBNull.Value;
                                    cmd.Parameters["@ToRemind"].Value = rec.ToRemind;
                                    cmd.Parameters["@ToRemindDate"].Value = rec.ToRemindDate.HasValue ? (object)rec.ToRemindDate.Value : DBNull.Value;
                                    cmd.Parameters["@FirstClaimDate"].Value = rec.FirstClaimDate.HasValue ? (object)rec.FirstClaimDate.Value : DBNull.Value;
                                    cmd.Parameters["@LastModified"].Value = nowUtc;
                                    cmd.Parameters["@ModifiedBy"].Value = _currentUser;
                                    cmd.Parameters["@ID"].Value = rec.ID;
                                    
                                    await cmd.ExecuteNonQueryAsync();
                                    updateCount++;
                                    
                                    // Periodic progress log
                                    if (updateCount % 10000 == 0)
                                        LogManager.Info($"[PERF] DB update progress: {updateCount}/{staged.Count} records updated");
                                }
                            }
                            
                            tx.Commit();
                            dbUpdateTimer.Stop();
                            LogManager.Info($"[PERF] DB updates completed: {staged.Count} records in {dbUpdateTimer.ElapsedMilliseconds}ms");
                        }
                        catch
                        {
                            tx.Rollback();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Failed to apply rules to existing records: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Maps a Reconciliation object from a DataReader
        /// </summary>
        private Reconciliation MapReconciliationFromReader(System.Data.Common.DbDataReader reader)
        {
            try
            {
                return new Reconciliation
                {
                    ID = reader["ID"]?.ToString(),
                    DWINGS_GuaranteeID = reader["DWINGS_GuaranteeID"]?.ToString(),
                    DWINGS_InvoiceID = reader["DWINGS_InvoiceID"]?.ToString(),
                    DWINGS_BGPMT = reader["DWINGS_BGPMT"]?.ToString(),
                    Action = reader["Action"] as int?,
                    ActionStatus = reader["ActionStatus"] as bool?,
                    ActionDate = reader["ActionDate"] as DateTime?,
                    Comments = reader["Comments"]?.ToString(),
                    InternalInvoiceReference = reader["InternalInvoiceReference"]?.ToString(),
                    FirstClaimDate = reader["FirstClaimDate"] as DateTime?,
                    LastClaimDate = reader["LastClaimDate"] as DateTime?,
                    ToRemind = (reader["ToRemind"] as bool?) ?? false,
                    SwiftCode = reader["SwiftCode"]?.ToString(),
                    PaymentReference = reader["PaymentReference"]?.ToString(),
                    KPI = reader["KPI"] as int?,
                    IncidentType = reader["IncidentType"] as int?,
                    RiskyItem = reader["RiskyItem"] as bool?,
                    ReasonNonRisky = reader["ReasonNonRisky"] as int?,
                    TriggerDate = reader["TriggerDate"] as DateTime?,
                    CreationDate = reader["CreationDate"] as DateTime?,
                    ModifiedBy = reader["ModifiedBy"]?.ToString(),
                    LastModified = reader["LastModified"] as DateTime?
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
