using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Services;

namespace RecoTool.Services.Rules
{
    /// <summary>
    /// Central rules engine evaluating truth-table rules.
    /// </summary>
    public class RulesEngine
    {
        // DEBUG FLAG: Set to true to log detailed rule matching failures
        private const bool DEBUG_RULES = true;
        
        private readonly TruthTableRepository _repo;
        private List<TruthRule> _cache;
        private DateTime _cacheTimeUtc;
        private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(2);

        public RulesEngine(OfflineFirstService offlineFirstService)
        {
            if (offlineFirstService == null) throw new ArgumentNullException(nameof(offlineFirstService));
            _repo = new TruthTableRepository(offlineFirstService);
        }

        /// <summary>
        /// Invalidate the in-memory rules cache so next evaluation reloads from repository.
        /// </summary>
        public void InvalidateCache()
        {
            _cache = null;
            _cacheTimeUtc = DateTime.MinValue;
        }

        private async Task<List<TruthRule>> GetRulesAsync(CancellationToken token)
        {
            if (_cache != null && DateTime.UtcNow - _cacheTimeUtc < _cacheTtl)
                return _cache;
            var rules = await _repo.LoadRulesAsync(token).ConfigureAwait(false);
            _cache = rules ?? new List<TruthRule>();
            _cacheTimeUtc = DateTime.UtcNow;
            return _cache;
        }

        /// <summary>
        /// Evaluate a single row context against rules for the desired scope.
        /// Returns the best matching rule (lowest Priority) with its outputs packaged in a result.
        /// Returns null when no rule matches.
        /// </summary>
        public async Task<RuleEvaluationResult> EvaluateAsync(RuleContext ctx, RuleScope scope, CancellationToken token = default)
        {
            if (ctx == null) return null;
            var rules = await GetRulesAsync(token).ConfigureAwait(false);
            if (rules == null || rules.Count == 0) return null;

            // Pre-normalize context
            var c = NormalizeContext(ctx);

            foreach (var r in rules)
            {
                if (r == null || !r.Enabled) continue;
                if (r.Scope != RuleScope.Both && r.Scope != scope) continue;
                
                bool matches;
                
                // DEBUG MODE: Show detailed failure reasons (commented for performance)
                // List<string> failures = null;
                // if (DEBUG_RULES)
                // {
                //     matches = MatchesWithDebug(r, c, out failures);
                //     if (!matches)
                //     {
                //         System.Diagnostics.Debug.WriteLine($"[RulesEngine] Rule {r.RuleId} ({r.Name}) did NOT match. Failures:");
                //         foreach (var fail in failures)
                //             System.Diagnostics.Debug.WriteLine($"  - {fail}");
                //     }
                // }
                // else
                // {
                //     matches = Matches(r, c);
                // }
                
                matches = Matches(r, c);
                
                if (!matches) continue;

                // Rule matched!
                if (DEBUG_RULES)
                {
                    System.Diagnostics.Debug.WriteLine($"[RulesEngine] ✓ Rule APPLIED: RuleId={r.RuleId}, Kpi='{r.OutputKpiId}', Action={r.OutputActionId}");
                }

                var res = new RuleEvaluationResult
                {
                    Rule = r,
                    NewActionIdSelf = r.OutputActionId,
                    NewKpiIdSelf = r.OutputKpiId,
                    NewIncidentTypeIdSelf = r.OutputIncidentTypeId,
                    NewRiskyItemSelf = r.OutputRiskyItem,
                    NewReasonNonRiskyIdSelf = r.OutputReasonNonRiskyId,
                    NewToRemindSelf = r.OutputToRemind,
                    NewToRemindDaysSelf = r.OutputToRemindDays,
                    NewActionDoneSelf = r.OutputActionDone.HasValue
                         ? (r.OutputActionDone.Value ? 1 : 0)
                         : (int?)null,

                    NewFirstClaimTodaySelf = r.OutputFirstClaimToday,
                    RequiresUserConfirm = !string.IsNullOrWhiteSpace(r.Message),
                    UserMessage = r.Message
                };
                return res;
            }
            
            // No rule matched
            if (DEBUG_RULES)
            {
                System.Diagnostics.Debug.WriteLine($"[RulesEngine] ✗ NO RULE APPLIED (IsPivot={c.IsPivot}, TxType={c.TransactionType}, IsGrouped={c.IsGrouped})");
            }
            
            return null;
        }

        /// <summary>
        /// Evaluate all rules for debugging purposes.
        /// Returns detailed information about each rule and why it matched or didn't match.
        /// </summary>
        public async Task<List<RuleDebugEvaluation>> EvaluateAllForDebugAsync(RuleContext ctx, RuleScope scope, CancellationToken token = default)
        {
            var results = new List<RuleDebugEvaluation>();
            if (ctx == null) return results;
            
            var rules = await GetRulesAsync(token).ConfigureAwait(false);
            if (rules == null || rules.Count == 0) return results;

            var c = NormalizeContext(ctx);

            foreach (var r in rules)
            {
                if (r == null) continue;

                var conditions = EvaluateConditions(r, c);
                bool allMet = conditions.TrueForAll(cd => cd.IsMet);

                results.Add(new RuleDebugEvaluation
                {
                    Rule = r,
                    IsEnabled = r.Enabled,
                    Conditions = conditions,
                    IsMatch = r.Enabled && allMet
                });
            }

            return results;
        }

        #region Unified condition evaluation

        /// <summary>
        /// Single source of truth for all condition checks.
        /// Returns a list of evaluated conditions (only non-wildcard conditions are included).
        /// A rule matches when all returned conditions have IsMet == true.
        /// </summary>
        private static List<RuleConditionDebug> EvaluateConditions(TruthRule r, RuleContext c)
        {
            var list = new List<RuleConditionDebug>();

            // TriggerOnField: if the rule restricts to a specific edited field, check it first
            if (!string.IsNullOrWhiteSpace(r.TriggerOnField))
            {
                bool fieldMatch = !string.IsNullOrWhiteSpace(c.EditedField)
                    && MatchesSet(r.TriggerOnField, c.EditedField);
                list.Add(new RuleConditionDebug { Field = "TriggerOnField", Expected = r.TriggerOnField, Actual = c.EditedField ?? "(null)", IsMet = fieldMatch });
            }

            // AccountSide
            if (!IsWildcard(r.AccountSide))
            {
                bool needP = r.AccountSide.Equals("P", StringComparison.OrdinalIgnoreCase);
                bool needR = r.AccountSide.Equals("R", StringComparison.OrdinalIgnoreCase);
                list.Add(new RuleConditionDebug { Field = "AccountSide", Expected = r.AccountSide, Actual = c.IsPivot ? "P" : "R", IsMet = (needP && c.IsPivot) || (needR && !c.IsPivot) });
            }

            // Booking (country)
            if (!IsWildcard(r.Booking))
                list.Add(new RuleConditionDebug { Field = "Booking", Expected = r.Booking, Actual = c.CountryId ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.CountryId) && MatchesSet(r.Booking, c.CountryId) });

            // GuaranteeType
            if (!IsWildcard(r.GuaranteeType))
                list.Add(new RuleConditionDebug { Field = "GuaranteeType", Expected = r.GuaranteeType, Actual = c.GuaranteeType ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.GuaranteeType) && MatchesSet(r.GuaranteeType, c.GuaranteeType) });

            // TransactionType
            if (!IsWildcard(r.TransactionType))
                list.Add(new RuleConditionDebug { Field = "TransactionType", Expected = r.TransactionType, Actual = c.TransactionType ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.TransactionType) && MatchesSet(r.TransactionType, c.TransactionType) });

            // HasDwingsLink
            if (r.HasDwingsLink.HasValue)
                list.Add(new RuleConditionDebug { Field = "HasDwingsLink", Expected = r.HasDwingsLink.Value.ToString(), Actual = c.HasDwingsLink?.ToString() ?? "(null)", IsMet = c.HasDwingsLink == r.HasDwingsLink.Value });

            // IsGrouped
            if (r.IsGrouped.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsGrouped", Expected = r.IsGrouped.Value.ToString(), Actual = c.IsGrouped?.ToString() ?? "(null)", IsMet = c.IsGrouped == r.IsGrouped.Value });

            // IsAmountMatch
            if (r.IsAmountMatch.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsAmountMatch", Expected = r.IsAmountMatch.Value.ToString(), Actual = c.IsAmountMatch?.ToString() ?? "(null)", IsMet = c.IsAmountMatch == r.IsAmountMatch.Value });

            // MissingAmount range
            if (r.MissingAmountMin.HasValue || r.MissingAmountMax.HasValue)
            {
                bool met = c.MissingAmount.HasValue;
                if (met)
                {
                    var a = c.MissingAmount.Value;
                    if (r.MissingAmountMin.HasValue && a < r.MissingAmountMin.Value) met = false;
                    if (r.MissingAmountMax.HasValue && a > r.MissingAmountMax.Value) met = false;
                }
                list.Add(new RuleConditionDebug { Field = "MissingAmount", Expected = $"[{r.MissingAmountMin?.ToString() ?? "∞"}, {r.MissingAmountMax?.ToString() ?? "∞"}]", Actual = c.MissingAmount?.ToString("F2") ?? "(null)", IsMet = met });
            }

            // Sign
            if (!IsWildcard(r.Sign))
                list.Add(new RuleConditionDebug { Field = "Sign", Expected = r.Sign, Actual = c.Sign ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.Sign) && r.Sign.Equals(c.Sign, StringComparison.OrdinalIgnoreCase) });

            // MTStatus
            if (r.MTStatus != MtStatusCondition.Wildcard)
                list.Add(new RuleConditionDebug { Field = "MTStatus", Expected = r.MTStatus.ToString(), Actual = c.MtStatus ?? "(null)", IsMet = MatchesMtStatus(r.MTStatus, c.MtStatus) });

            // CommIdEmail
            if (r.CommIdEmail.HasValue)
                list.Add(new RuleConditionDebug { Field = "CommIdEmail", Expected = r.CommIdEmail.Value.ToString(), Actual = c.HasCommIdEmail?.ToString() ?? "(null)", IsMet = c.HasCommIdEmail.HasValue && c.HasCommIdEmail.Value == r.CommIdEmail.Value });

            // BgiStatusInitiated
            if (r.BgiStatusInitiated.HasValue)
                list.Add(new RuleConditionDebug { Field = "BgiStatusInitiated", Expected = r.BgiStatusInitiated.Value.ToString(), Actual = c.IsBgiInitiated?.ToString() ?? "(null)", IsMet = c.IsBgiInitiated.HasValue && c.IsBgiInitiated.Value == r.BgiStatusInitiated.Value });

            // PaymentRequestStatus
            if (!IsWildcard(r.PaymentRequestStatus))
                list.Add(new RuleConditionDebug { Field = "PaymentRequestStatus", Expected = r.PaymentRequestStatus, Actual = c.PaymentRequestStatus ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.PaymentRequestStatus) && MatchesSet(r.PaymentRequestStatus, c.PaymentRequestStatus) });

            // InvoiceStatus
            if (!IsWildcard(r.InvoiceStatus))
                list.Add(new RuleConditionDebug { Field = "InvoiceStatus", Expected = r.InvoiceStatus, Actual = c.InvoiceStatus ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.InvoiceStatus) && MatchesSet(r.InvoiceStatus, c.InvoiceStatus) });

            // TriggerDateIsNull
            if (r.TriggerDateIsNull.HasValue)
                list.Add(new RuleConditionDebug { Field = "TriggerDateIsNull", Expected = r.TriggerDateIsNull.Value.ToString(), Actual = c.TriggerDateIsNull?.ToString() ?? "(null)", IsMet = c.TriggerDateIsNull.HasValue && c.TriggerDateIsNull.Value == r.TriggerDateIsNull.Value });

            // DaysSinceTrigger range
            if (r.DaysSinceTriggerMin.HasValue || r.DaysSinceTriggerMax.HasValue)
            {
                bool met = c.DaysSinceTrigger.HasValue;
                if (met) { var d = c.DaysSinceTrigger.Value; if (r.DaysSinceTriggerMin.HasValue && d < r.DaysSinceTriggerMin.Value) met = false; if (r.DaysSinceTriggerMax.HasValue && d > r.DaysSinceTriggerMax.Value) met = false; }
                list.Add(new RuleConditionDebug { Field = "DaysSinceTrigger", Expected = $"[{r.DaysSinceTriggerMin?.ToString() ?? "∞"}, {r.DaysSinceTriggerMax?.ToString() ?? "∞"}]", Actual = c.DaysSinceTrigger?.ToString() ?? "(null)", IsMet = met });
            }

            // OperationDaysAgo range
            if (r.OperationDaysAgoMin.HasValue || r.OperationDaysAgoMax.HasValue)
            {
                bool met = c.OperationDaysAgo.HasValue;
                if (met) { var d = c.OperationDaysAgo.Value; if (r.OperationDaysAgoMin.HasValue && d < r.OperationDaysAgoMin.Value) met = false; if (r.OperationDaysAgoMax.HasValue && d > r.OperationDaysAgoMax.Value) met = false; }
                list.Add(new RuleConditionDebug { Field = "OperationDaysAgo", Expected = $"[{r.OperationDaysAgoMin?.ToString() ?? "∞"}, {r.OperationDaysAgoMax?.ToString() ?? "∞"}]", Actual = c.OperationDaysAgo?.ToString() ?? "(null)", IsMet = met });
            }

            // IsMatched
            if (r.IsMatched.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsMatched", Expected = r.IsMatched.Value.ToString(), Actual = c.IsMatched?.ToString() ?? "(null)", IsMet = c.IsMatched.HasValue && c.IsMatched.Value == r.IsMatched.Value });

            // HasManualMatch
            if (r.HasManualMatch.HasValue)
                list.Add(new RuleConditionDebug { Field = "HasManualMatch", Expected = r.HasManualMatch.Value.ToString(), Actual = c.HasManualMatch?.ToString() ?? "(null)", IsMet = c.HasManualMatch.HasValue && c.HasManualMatch.Value == r.HasManualMatch.Value });

            // IsFirstRequest
            if (r.IsFirstRequest.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsFirstRequest", Expected = r.IsFirstRequest.Value.ToString(), Actual = c.IsFirstRequest?.ToString() ?? "(null)", IsMet = c.IsFirstRequest.HasValue && c.IsFirstRequest.Value == r.IsFirstRequest.Value });

            // IsNewLine
            if (r.IsNewLine.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsNewLine", Expected = r.IsNewLine.Value.ToString(), Actual = c.IsNewLine?.ToString() ?? "(null)", IsMet = c.IsNewLine.HasValue && c.IsNewLine.Value == r.IsNewLine.Value });

            // DaysSinceReminder range
            if (r.DaysSinceReminderMin.HasValue || r.DaysSinceReminderMax.HasValue)
            {
                bool met = c.DaysSinceReminder.HasValue;
                if (met) { var d = c.DaysSinceReminder.Value; if (r.DaysSinceReminderMin.HasValue && d < r.DaysSinceReminderMin.Value) met = false; if (r.DaysSinceReminderMax.HasValue && d > r.DaysSinceReminderMax.Value) met = false; }
                list.Add(new RuleConditionDebug { Field = "DaysSinceReminder", Expected = $"[{r.DaysSinceReminderMin?.ToString() ?? "∞"}, {r.DaysSinceReminderMax?.ToString() ?? "∞"}]", Actual = c.DaysSinceReminder?.ToString() ?? "(null)", IsMet = met });
            }

            // CurrentActionId (multi-value)
            if (!IsWildcard(r.CurrentActionId))
                list.Add(new RuleConditionDebug { Field = "CurrentActionId", Expected = r.CurrentActionId, Actual = c.CurrentActionId?.ToString() ?? "(null)", IsMet = c.CurrentActionId.HasValue && MatchesSet(r.CurrentActionId, c.CurrentActionId.Value.ToString()) });

            // IsActionDone
            if (r.IsActionDone.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsActionDone", Expected = r.IsActionDone.Value.ToString(), Actual = c.IsActionDone?.ToString() ?? "(null)", IsMet = c.IsActionDone.HasValue && c.IsActionDone.Value == r.IsActionDone.Value });

            return list;
        }

        /// <summary>Fast-path: returns true/false without allocating debug info.</summary>
        private static bool Matches(TruthRule r, RuleContext c)
        {
            var conditions = EvaluateConditions(r, c);
            return conditions.TrueForAll(cd => cd.IsMet);
        }

        #endregion

        #region Normalization helpers

        private static RuleContext NormalizeContext(RuleContext ctx)
        {
            return new RuleContext
            {
                CountryId = ctx.CountryId,
                IsPivot = ctx.IsPivot,
                HasDwingsLink = ctx.HasDwingsLink,
                IsGrouped = ctx.IsGrouped,
                IsAmountMatch = ctx.IsAmountMatch,
                MissingAmount = ctx.MissingAmount,
                Bgi = string.IsNullOrWhiteSpace(ctx.Bgi) ? null : ctx.Bgi.Trim(),
                Sign = NormalizeSign(ctx.Sign),
                GuaranteeType = NormalizeGuaranteeType(ctx.GuaranteeType),
                TransactionType = NormalizeTransactionType(ctx.TransactionType),
                TriggerDateIsNull = ctx.TriggerDateIsNull,
                DaysSinceTrigger = ctx.DaysSinceTrigger,
                OperationDaysAgo = ctx.OperationDaysAgo,
                IsMatched = ctx.IsMatched,
                HasManualMatch = ctx.HasManualMatch,
                IsFirstRequest = ctx.IsFirstRequest,
                IsNewLine = ctx.IsNewLine,
                DaysSinceReminder = ctx.DaysSinceReminder,
                CurrentActionId = ctx.CurrentActionId,
                IsActionDone = ctx.IsActionDone,
                MtStatus = ctx.MtStatus,
                HasCommIdEmail = ctx.HasCommIdEmail,
                IsBgiInitiated = ctx.IsBgiInitiated,
                PaymentRequestStatus = ctx.PaymentRequestStatus,
                InvoiceStatus = ctx.InvoiceStatus,
                EditedField = ctx.EditedField
            };
        }

        private static string NormalizeSign(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().ToUpperInvariant();
            if (s.StartsWith("D")) return "D";
            if (s.StartsWith("C")) return "C";
            return s;
        }

        private static string NormalizeGuaranteeType(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().ToUpperInvariant();
            if (s.StartsWith("REISSU")) return "REISSUANCE";
            if (s.StartsWith("ISSU")) return "ISSUANCE";
            if (s.StartsWith("NOTIF") || s.StartsWith("ADVISING")) return "ADVISING";
            return s;
        }

        private static string NormalizeTransactionType(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().ToUpperInvariant().Replace(' ', '_');
            return s;
        }

        private static bool MatchesMtStatus(MtStatusCondition condition, string actualMtStatus)
        {
            switch (condition)
            {
                case MtStatusCondition.Wildcard:
                    return true;
                case MtStatusCondition.Acked:
                    return !string.IsNullOrWhiteSpace(actualMtStatus) && 
                           (string.Equals(actualMtStatus, "ACKED", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(actualMtStatus, "SENT", StringComparison.OrdinalIgnoreCase));
                case MtStatusCondition.NotAcked:
                    return !string.IsNullOrWhiteSpace(actualMtStatus) && 
                           !string.Equals(actualMtStatus, "ACKED", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(actualMtStatus, "SENT", StringComparison.OrdinalIgnoreCase);
                case MtStatusCondition.Null:
                    return string.IsNullOrWhiteSpace(actualMtStatus);
                default:
                    return false;
            }
        }

        private static bool IsWildcard(string s)
        {
            return string.IsNullOrWhiteSpace(s) || s.Trim() == "*";
        }

        private static bool MatchesSet(string ruleValue, string ctxValue)
        {
            if (string.IsNullOrWhiteSpace(ruleValue)) return false;
            var parts = ruleValue.Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(x => x.Trim().ToUpperInvariant()).ToList();
            var val = (ctxValue ?? string.Empty).Trim().ToUpperInvariant();
            return parts.Contains(val);
        }

        #endregion
    }

    /// <summary>
    /// Debug evaluation result for a single rule
    /// </summary>
    public class RuleDebugEvaluation
    {
        public TruthRule Rule { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsMatch { get; set; }
        public List<RuleConditionDebug> Conditions { get; set; }
    }

    /// <summary>
    /// Debug information for a single condition
    /// </summary>
    public class RuleConditionDebug
    {
        public string Field { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
        public bool IsMet { get; set; }
    }
}
