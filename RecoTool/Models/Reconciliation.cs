using System;
using RecoTool.Services;

namespace RecoTool.Models
{
    /// <summary>
    /// Modèle pour les informations de réconciliation
    /// Table T_Reconciliation
    /// </summary>
    public class Reconciliation : BaseEntity
    {
        public string ID { get; set; }
        public string DWINGS_GuaranteeID { get; set; }
        public string DWINGS_InvoiceID { get; set; }
        public string DWINGS_BGPMT { get; set; }
        public int? Action { get; set; }
        // New: assignee user ID (referential T_User.USR_ID)
        public string Assignee { get; set; }
        public string Comments { get; set; }
        public string InternalInvoiceReference { get; set; }
        public DateTime? FirstClaimDate { get; set; }
        public DateTime? LastClaimDate { get; set; }
        public bool ToRemind { get; set; }
        public DateTime? ToRemindDate { get; set; }
        public bool ACK { get; set; }
        public string SwiftCode { get; set; }
        public string PaymentReference { get; set; }
        // New long text fields for reconciliation notes
        public string MbawData { get; set; }
        public string SpiritData { get; set; }
        public int? KPI { get; set; }
        public int? IncidentType { get; set; }
        public bool? RiskyItem { get; set; }
        public int? ReasonNonRisky { get; set; }
        
        // Incident Number
        public string IncNumber { get; set; }
        
        // Trigger date
        public DateTime? TriggerDate { get; set; }

        // Phase 2: Partially-paid BGI tracking. When > 0, the row is treated as "still owed"
        // and is excluded from the bulk Trigger flow (RowActions.DwingsBlueButton) until the
        // user clears it (= sets to 0 or null). NULL means the field has never been set —
        // legacy rows behave exactly as before. Stored as CURRENCY in Access for fixed-point
        // money precision.
        public decimal? RemainingAmount { get; set; }

        // Action workflow status: false => PENDING, true => DONE
        public bool? ActionStatus { get; set; }
        // Date of status modification (set when Action is set or status changes)
        public DateTime? ActionDate { get; set; }

        // --- Audit & user-edit protection (Phase 1 robustness) ---

        /// <summary>
        /// Timestamp of the last modification made by a user through the UI (not by a rule).
        /// Used together with <see cref="UserEditedFields"/> to enforce the user-edit lock against rules.
        /// </summary>
        public DateTime? LastModifiedByUser { get; set; }

        /// <summary>
        /// Pipe-separated list of fields manually edited by the user (e.g. "Action|KPI|IncidentType").
        /// A rule with <c>RespectUserEdits = true</c> cannot overwrite any of these fields
        /// while <see cref="LastModifiedByUser"/> is within the lock window.
        /// </summary>
        public string UserEditedFields { get; set; }

        /// <summary>
        /// RuleId of the last rule that modified this reconciliation (any scope).
        /// Null if the row was never touched by a rule.
        /// </summary>
        public string LastRuleAppliedId { get; set; }

        /// <summary>
        /// Timestamp of the last rule application.
        /// </summary>
        public DateTime? LastRuleAppliedAt { get; set; }

        /// <summary>
        /// Effective risky flag for business logic: null is considered false.
        /// </summary>
        public bool IsRiskyEffective => RiskyItem == true;

        /// <summary>
        /// Crée une nouvelle réconciliation liée à une ligne Ambre
        /// </summary>
        /// <param name="ambreLineId">ID de la ligne Ambre correspondante</param>
        /// <returns>Nouvelle instance de Reconciliation</returns>
        public static Reconciliation CreateForAmbreLine(string ambreLineId)
        {
            return new Reconciliation
            {
                // Utiliser uniquement l'ID comme clé primaire stable
                ID = ambreLineId
            };
        }

        /// <summary>
        /// Indique si cette réconciliation nécessite un rappel
        /// </summary>
        public bool RequiresReminder => ToRemind && ToRemindDate.HasValue && ToRemindDate <= DateTime.Today;

        /// <summary>
        /// Indique si cette réconciliation a des informations DWINGS associées
        /// </summary>
        public bool HasDWINGSData => !string.IsNullOrEmpty(DWINGS_GuaranteeID) || 
                                     !string.IsNullOrEmpty(DWINGS_InvoiceID) || 
                                     !string.IsNullOrEmpty(DWINGS_BGPMT);
    }
}
