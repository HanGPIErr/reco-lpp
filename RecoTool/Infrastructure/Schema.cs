namespace RecoTool.Infrastructure
{
    /// <summary>
    /// Single source of truth for Access/OleDb table and column names referenced from
    /// hand-written SQL strings in the RecoTool codebase.
    ///
    /// <para>
    /// <b>Intent</b> — historically, every SQL string in <c>Services\*</c> and
    /// <c>Infrastructure\*</c> embedded table and column names as raw string literals
    /// (e.g. <c>"SELECT * FROM T_Reconciliation"</c>). Renaming a column in the database
    /// then required a grep-and-replace over the whole solution with no compile-time
    /// safety net. This class centralizes those names as <c>public const string</c>
    /// values so that:
    /// </para>
    /// <list type="bullet">
    ///   <item>A future rename can be performed via Visual Studio's <i>Find All References</i>
    ///         / rename refactor on the constant, with compile errors surfacing any
    ///         consumer that has not been migrated yet.</item>
    ///   <item>Grep over the codebase remains safe: searching for the constant name
    ///         (e.g. <c>Schema.Columns.Reconciliation.DWINGS_InvoiceID</c>) finds every
    ///         consumer, including dynamic SQL builders.</item>
    ///   <item>Typos in column names become compile-time errors instead of runtime
    ///         OleDb exceptions buried inside try/catch in production.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Migration strategy</b> — this class is published <i>before</i> consumers are
    /// migrated. Existing call sites keep using raw strings; new code (and PRs that
    /// touch a query for unrelated reasons) is encouraged to switch to the constants.
    /// Once all consumers are migrated, the raw-string equivalents can be removed.
    /// </para>
    ///
    /// <para>
    /// <b>Casing rule</b> — every constant value MUST exactly match what currently
    /// appears in the SQL strings. Access/Jet is case-insensitive at runtime but C# is
    /// not — keep the canonical casing used by <see cref="Services.DatabaseRecreationService"/>
    /// when it differs from a local SQL string (e.g. <c>T_Param</c>, not <c>T_param</c>).
    /// </para>
    /// </summary>
    public static class Schema
    {
        /// <summary>
        /// Table names. One <c>public const string</c> per table referenced from SQL.
        /// </summary>
        public static class Tables
        {
            // Country databases (per-country .accdb)
            public const string T_Data_Ambre = "T_Data_Ambre";
            public const string T_Reconciliation = "T_Reconciliation";

            // Reconciliation-side audit/history
            public const string T_ReconciliationChangeJournal = "T_ReconciliationChangeJournal";
            public const string T_ImportRun = "T_ImportRun";

            // Reconciliation-side rules engine
            public const string T_Reco_Rules = "T_Reco_Rules";
            public const string T_RuleProposals = "T_RuleProposals";

            // Sync infrastructure
            public const string T_SyncChangeLog = "T_SyncChangeLog";

            // DWINGS unified database (per-country)
            public const string T_DW_Guarantee = "T_DW_Guarantee";
            public const string T_DW_Data = "T_DW_Data";

            // Referential database (shared)
            public const string T_Param = "T_Param";
            public const string T_User = "T_User";
            public const string T_Ref_Country = "T_Ref_Country";
            public const string T_Ref_Ambre_ImportFields = "T_Ref_Ambre_ImportFields";
            public const string T_Ref_Ambre_TransactionCodes = "T_Ref_Ambre_TransactionCodes";
            public const string T_Ref_Ambre_Transform = "T_Ref_Ambre_Transform";
            public const string T_Ref_TodoList = "T_Ref_TodoList";
            public const string T_Ref_User_Fields = "T_Ref_User_Fields";
            public const string T_Ref_User_Fields_Preference = "T_Ref_User_Fields_Preference";
            public const string T_Ref_User_Filter = "T_Ref_User_Filter";
        }

        /// <summary>
        /// Column names grouped by owning table. Each inner static class corresponds to
        /// one entry in <see cref="Tables"/>. Only columns referenced from hand-written
        /// SQL strings are exposed (DTO-only properties that never appear in a SQL
        /// literal are intentionally omitted).
        /// </summary>
        public static class Columns
        {
            /// <summary>
            /// Columns on <see cref="Tables.T_Data_Ambre"/>.
            /// </summary>
            public static class Ambre
            {
                public const string ID = "ID";
                public const string Account_ID = "Account_ID";
                public const string CCY = "CCY";
                public const string Country = "Country";
                public const string Event_Num = "Event_Num";
                public const string Folder = "Folder";
                public const string Pivot_MbawIDFromLabel = "Pivot_MbawIDFromLabel";
                public const string Pivot_TransactionCodesFromLabel = "Pivot_TransactionCodesFromLabel";
                public const string Pivot_TRNFromLabel = "Pivot_TRNFromLabel";
                public const string RawLabel = "RawLabel";
                public const string Receivable_DWRefFromAmbre = "Receivable_DWRefFromAmbre";
                public const string LocalSignedAmount = "LocalSignedAmount";
                public const string Operation_Date = "Operation_Date";
                public const string Reconciliation_Num = "Reconciliation_Num";
                public const string Receivable_InvoiceFromAmbre = "Receivable_InvoiceFromAmbre";
                public const string ReconciliationOrigin_Num = "ReconciliationOrigin_Num";
                public const string SignedAmount = "SignedAmount";
                public const string Value_Date = "Value_Date";

                // BaseEntity audit columns
                public const string CreationDate = "CreationDate";
                public const string DeleteDate = "DeleteDate";
                public const string ModifiedBy = "ModifiedBy";
                public const string LastModified = "LastModified";
                public const string Version = "Version";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Reconciliation"/>.
            /// </summary>
            public static class Reconciliation
            {
                public const string ID = "ID";
                public const string DWINGS_GuaranteeID = "DWINGS_GuaranteeID";
                public const string DWINGS_InvoiceID = "DWINGS_InvoiceID";
                public const string DWINGS_BGPMT = "DWINGS_BGPMT";
                public const string Action = "Action";
                public const string ActionStatus = "ActionStatus";
                public const string ActionDate = "ActionDate";
                public const string Assignee = "Assignee";
                public const string Comments = "Comments";
                public const string InternalInvoiceReference = "InternalInvoiceReference";
                public const string FirstClaimDate = "FirstClaimDate";
                public const string LastClaimDate = "LastClaimDate";
                public const string ToRemind = "ToRemind";
                public const string ToRemindDate = "ToRemindDate";
                public const string ACK = "ACK";
                public const string SwiftCode = "SwiftCode";
                public const string PaymentReference = "PaymentReference";
                public const string MbawData = "MbawData";
                public const string SpiritData = "SpiritData";
                public const string KPI = "KPI";
                public const string IncidentType = "IncidentType";
                public const string RiskyItem = "RiskyItem";
                public const string ReasonNonRisky = "ReasonNonRisky";
                public const string TriggerDate = "TriggerDate";
                public const string RemainingAmount = "RemainingAmount";

                // Audit & user-edit protection
                public const string LastModifiedByUser = "LastModifiedByUser";
                public const string UserEditedFields = "UserEditedFields";
                public const string LastRuleAppliedId = "LastRuleAppliedId";
                public const string LastRuleAppliedAt = "LastRuleAppliedAt";

                // BaseEntity audit columns
                public const string CreationDate = "CreationDate";
                public const string DeleteDate = "DeleteDate";
                public const string ModifiedBy = "ModifiedBy";
                public const string LastModified = "LastModified";
                public const string Version = "Version";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_DW_Guarantee"/>.
            /// </summary>
            public static class DwingsGuarantee
            {
                public const string GUARANTEE_ID = "GUARANTEE_ID";
                public const string BOOKING = "BOOKING";
                public const string GUARANTEE_STATUS = "GUARANTEE_STATUS";
                public const string NATURE = "NATURE";
                public const string EVENT_STATUS = "EVENT_STATUS";
                public const string EVENT_EFFECTIVEDATE = "EVENT_EFFECTIVEDATE";
                public const string ISSUEDATE = "ISSUEDATE";
                public const string OFFICIALREF = "OFFICIALREF";
                public const string UNDERTAKINGEVENT = "UNDERTAKINGEVENT";
                public const string PROCESS = "PROCESS";
                public const string EXPIRYDATETYPE = "EXPIRYDATETYPE";
                public const string EXPIRYDATE = "EXPIRYDATE";
                public const string PARTY_ID = "PARTY_ID";
                public const string PARTY_REF = "PARTY_REF";
                public const string SECONDARY_OBLIGOR = "SECONDARY_OBLIGOR";
                public const string SECONDARY_OBLIGOR_NATURE = "SECONDARY_OBLIGOR_NATURE";
                public const string ROLE = "ROLE";
                public const string COUNTRY = "COUNTRY";
                public const string CENTRAL_PARTY_CODE = "CENTRAL_PARTY_CODE";
                public const string NAME1 = "NAME1";
                public const string NAME2 = "NAME2";
                public const string GROUPE = "GROUPE";
                public const string PREMIUM = "PREMIUM";
                public const string BRANCH_CODE = "BRANCH_CODE";
                public const string BRANCH_NAME = "BRANCH_NAME";
                public const string OUTSTANDING_AMOUNT = "OUTSTANDING_AMOUNT";
                public const string OUTSTANDING_AMOUNT_IN_BOOKING_CURRENCY = "OUTSTANDING_AMOUNT_IN_BOOKING_CURRENCY";
                public const string CURRENCYNAME = "CURRENCYNAME";
                public const string CANCELLATIONDATE = "CANCELLATIONDATE";
                public const string CONTROLER = "CONTROLER";
                public const string AUTOMATICBOOKOFF = "AUTOMATICBOOKOFF";
                public const string NATUREOFDEAL = "NATUREOFDEAL";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_DW_Data"/> (DWINGS invoices/payment requests).
            /// </summary>
            public static class DwingsData
            {
                public const string BGPMT = "BGPMT";
                public const string INVOICE_ID = "INVOICE_ID";
                public const string BOOKING = "BOOKING";
                public const string REQUESTED_INVOICE_AMOUNT = "REQUESTED_INVOICE_AMOUNT";
                public const string SENDER_NAME = "SENDER_NAME";
                public const string RECEIVER_NAME = "RECEIVER_NAME";
                public const string SENDER_REFERENCE = "SENDER_REFERENCE";
                public const string RECEIVER_REFERENCE = "RECEIVER_REFERENCE";
                public const string T_INVOICE_STATUS = "T_INVOICE_STATUS";
                public const string BILLING_AMOUNT = "BILLING_AMOUNT";
                public const string BILLING_CURRENCY = "BILLING_CURRENCY";
                public const string START_DATE = "START_DATE";
                public const string END_DATE = "END_DATE";
                public const string FINAL_AMOUNT = "FINAL_AMOUNT";
                public const string T_COMMISSION_PERIOD_STATUS = "T_COMMISSION_PERIOD_STATUS";
                public const string BUSINESS_CASE_REFERENCE = "BUSINESS_CASE_REFERENCE";
                public const string BUSINESS_CASE_ID = "BUSINESS_CASE_ID";
                public const string POSTING_PERIODICITY = "POSTING_PERIODICITY";
                public const string EVENT_ID = "EVENT_ID";
                public const string COMMENTS = "COMMENTS";
                public const string SENDER_ACCOUNT_NUMBER = "SENDER_ACCOUNT_NUMBER";
                public const string SENDER_ACCOUNT_BIC = "SENDER_ACCOUNT_BIC";
                public const string RECEIVER_ACCOUNT_NUMBER = "RECEIVER_ACCOUNT_NUMBER";
                public const string RECEIVER_ACCOUNT_BIC = "RECEIVER_ACCOUNT_BIC";
                public const string REQUESTED_AMOUNT = "REQUESTED_AMOUNT";
                public const string EXECUTED_AMOUNT = "EXECUTED_AMOUNT";
                public const string REQUESTED_EXECUTION_DATE = "REQUESTED_EXECUTION_DATE";
                public const string T_PAYMENT_REQUEST_STATUS = "T_PAYMENT_REQUEST_STATUS";
                public const string DEBTOR_ACCOUNT_ID = "DEBTOR_ACCOUNT_ID";
                public const string CREDITOR_ACCOUNT_ID = "CREDITOR_ACCOUNT_ID";
                public const string MT_STATUS = "MT_STATUS";
                public const string REMINDER_NUMBER = "REMINDER_NUMBER";
                public const string ERROR_MESSAGE = "ERROR_MESSAGE";
                public const string DEBTOR_PARTY_ID = "DEBTOR_PARTY_ID";
                public const string PAYMENT_METHOD = "PAYMENT_METHOD";
                public const string PAYMENT_TYPE = "PAYMENT_TYPE";
                public const string DEBTOR_PARTY_NAME = "DEBTOR_PARTY_NAME";
                public const string DEBTOR_ACCOUNT_NUMBER = "DEBTOR_ACCOUNT_NUMBER";
                public const string CREDITOR_PARTY_ID = "CREDITOR_PARTY_ID";
                public const string CREDITOR_ACCOUNT_NUMBER = "CREDITOR_ACCOUNT_NUMBER";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Param"/>.
            /// </summary>
            public static class Param
            {
                public const string PAR_Key = "PAR_Key";
                public const string PAR_Value = "PAR_Value";
                public const string PAR_Description = "PAR_Description";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_User"/>.
            /// </summary>
            public static class User
            {
                public const string USR_ID = "USR_ID";
                public const string USR_Name = "USR_Name";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Ref_Country"/>.
            /// </summary>
            public static class Country
            {
                public const string CNT_Id = "CNT_Id";
                public const string CNT_Name = "CNT_Name";
                public const string CNT_AmbrePivot = "CNT_AmbrePivot";
                public const string CNT_AmbrePivotCountryId = "CNT_AmbrePivotCountryId";
                public const string CNT_AmbreReceivable = "CNT_AmbreReceivable";
                public const string CNT_AmbreReceivableCountryId = "CNT_AmbreReceivableCountryId";
                public const string CNT_ServiceCode = "CNT_ServiceCode";
                public const string CNT_BIC = "CNT_BIC";
                public const string CNT_DWID = "CNT_DWID";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Ref_Ambre_ImportFields"/>.
            /// </summary>
            public static class AmbreImportFields
            {
                public const string AMB_Source = "AMB_Source";
                public const string AMB_Destination = "AMB_Destination";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Ref_Ambre_TransactionCodes"/>.
            /// </summary>
            public static class AmbreTransactionCodes
            {
                public const string ATC_ID = "ATC_ID";
                public const string ATC_CODE = "ATC_CODE";
                public const string ATC_TAG = "ATC_TAG";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Ref_Ambre_Transform"/>.
            /// </summary>
            public static class AmbreTransform
            {
                public const string AMB_Source = "AMB_Source";
                public const string AMB_Destination = "AMB_Destination";
                public const string AMB_TransformationFunction = "AMB_TransformationFunction";
                public const string AMB_Description = "AMB_Description";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Ref_TodoList"/>.
            /// </summary>
            public static class TodoList
            {
                public const string TDL_id = "TDL_id";
                public const string TDL_Name = "TDL_Name";
                public const string TDL_FilterName = "TDL_FilterName";
                public const string TDL_ViewName = "TDL_ViewName";
                public const string TDL_Account = "TDL_Account";
                public const string TDL_Order = "TDL_Order";
                public const string TDL_Active = "TDL_Active";
                public const string TDL_CountryId = "TDL_CountryId";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Ref_User_Fields"/>.
            /// </summary>
            public static class UserFields
            {
                public const string USR_ID = "USR_ID";
                public const string USR_Category = "USR_Category";
                public const string USR_FieldName = "USR_FieldName";
                public const string USR_FieldDescription = "USR_FieldDescription";
                public const string USR_Pivot = "USR_Pivot";
                public const string USR_Receivable = "USR_Receivable";
                public const string USR_Color = "USR_Color";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Ref_User_Fields_Preference"/>.
            /// </summary>
            public static class UserFieldsPreference
            {
                public const string UPF_id = "UPF_id";
                public const string UPF_Name = "UPF_Name";
                public const string UPF_user = "UPF_user";
                public const string UPF_SQL = "UPF_SQL";
                public const string UPF_ColumnWidths = "UPF_ColumnWidths";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Ref_User_Filter"/>.
            /// </summary>
            public static class UserFilter
            {
                public const string UFI_id = "UFI_id";
                public const string UFI_Name = "UFI_Name";
                public const string UFI_SQL = "UFI_SQL";
                public const string UFI_CreatedBy = "UFI_CreatedBy";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_Reco_Rules"/>.
            /// Canonical schema is created by <c>TruthTableRepository.EnsureRulesTableAsync</c>.
            /// </summary>
            public static class RecoRules
            {
                public const string RuleId = "RuleId";
                public const string Enabled = "Enabled";
                public const string Priority = "Priority";
                public const string Scope = "Scope";
                public const string AccountSide = "AccountSide";
                public const string GuaranteeType = "GuaranteeType";
                public const string TransactionType = "TransactionType";
                public const string Booking = "Booking";
                public const string HasDwingsLink = "HasDwingsLink";
                public const string IsGrouped = "IsGrouped";
                public const string IsAmountMatch = "IsAmountMatch";
                public const string Sign = "Sign";
                public const string MTStatus = "MTStatus";
                public const string CommIdEmail = "CommIdEmail";
                public const string BgiStatusInitiated = "BgiStatusInitiated";
                public const string TriggerDateIsNull = "TriggerDateIsNull";
                public const string DaysSinceTriggerMin = "DaysSinceTriggerMin";
                public const string DaysSinceTriggerMax = "DaysSinceTriggerMax";
                public const string OperationDaysAgoMin = "OperationDaysAgoMin";
                public const string OperationDaysAgoMax = "OperationDaysAgoMax";
                public const string IsMatched = "IsMatched";
                public const string HasManualMatch = "HasManualMatch";
                public const string IsFirstRequest = "IsFirstRequest";
                public const string IsNewLine = "IsNewLine";
                public const string DaysSinceReminderMin = "DaysSinceReminderMin";
                public const string DaysSinceReminderMax = "DaysSinceReminderMax";
                public const string CurrentActionId = "CurrentActionId";
                public const string IsActionDone = "IsActionDone";
                public const string PaymentRequestStatus = "PaymentRequestStatus";
                public const string OutputActionId = "OutputActionId";
                public const string OutputKpiId = "OutputKpiId";
                public const string OutputIncidentTypeId = "OutputIncidentTypeId";
                public const string OutputRiskyItem = "OutputRiskyItem";
                public const string OutputReasonNonRiskyId = "OutputReasonNonRiskyId";
                public const string OutputToRemind = "OutputToRemind";
                public const string OutputToRemindDays = "OutputToRemindDays";
                public const string OutputActionDone = "OutputActionDone";
                public const string OutputFirstClaimToday = "OutputFirstClaimToday";
                public const string ApplyTo = "ApplyTo";
                public const string AutoApply = "AutoApply";
                public const string Message = "Message";
                public const string TriggerOnField = "TriggerOnField";
                public const string RespectUserEdits = "RespectUserEdits";
                public const string UserEditLockDays = "UserEditLockDays";
                public const string Mode = "Mode";
            }

            /// <summary>
            /// Columns on <see cref="Tables.T_RuleProposals"/>.
            /// Canonical schema is created by <c>RuleProposalRepository.EnsureTableAsync</c>.
            /// </summary>
            public static class RuleProposals
            {
                public const string ProposalId = "ProposalId";
                public const string RecoId = "RecoId";
                public const string RuleId = "RuleId";
                public const string Field = "Field";
                public const string OldValue = "OldValue";
                public const string NewValue = "NewValue";
                public const string CreatedAt = "CreatedAt";
                public const string CreatedBy = "CreatedBy";
                public const string Status = "Status";
                public const string DecidedBy = "DecidedBy";
                public const string DecidedAt = "DecidedAt";
                public const string DeleteDate = "DeleteDate";
            }
        }
    }
}
