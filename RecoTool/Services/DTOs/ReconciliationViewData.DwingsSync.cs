namespace RecoTool.Services.DTOs
{
    /// <summary>
    /// DWINGS synchronisation surface of <see cref="ReconciliationViewData"/>:
    /// <list type="bullet">
    /// <item><see cref="RefreshDwingsData"/> — public API invoked from the grid editing code-behind when
    /// the user manually changes <c>DWINGS_InvoiceID</c> or <c>DWINGS_GuaranteeID</c>.</item>
    /// <item><see cref="PopulateInvoiceProperties"/> / <see cref="PopulateGuaranteeProperties"/> — fill the
    /// <c>I_*</c> and <c>G_*</c> columns from a DTO (used during initial load and on refresh).</item>
    /// <item><see cref="NotifyAllDwingsProperties"/> — raises PropertyChanged for every DWINGS-derived column
    /// in one sweep so bound cells repaint without needing targeted setters.</item>
    /// </list>
    /// The lookup caches themselves live in <c>ReconciliationViewData.Caches.cs</c>.
    /// </summary>
    public partial class ReconciliationViewData
    {
        /// <summary>
        /// Reloads the DWINGS-derived columns (<c>I_*</c>, <c>G_*</c>) from the current caches using this
        /// row's <see cref="DataAmbre.DWINGS_InvoiceID"/> and <c>DWINGS_GuaranteeID</c> as the lookup keys,
        /// then notifies every dependent binding in one batch.
        /// Typically triggered after the user edits a DWINGS reference in the grid.
        /// </summary>
        public void RefreshDwingsData()
        {
            GetDwingsCacheSnapshots(out var invoiceCache, out var guaranteeCache);

            DwingsInvoiceDto invoice = null;
            DwingsGuaranteeDto guarantee = null;

            if (!string.IsNullOrWhiteSpace(DWINGS_InvoiceID) && invoiceCache != null)
                invoiceCache.TryGetValue(DWINGS_InvoiceID, out invoice);

            if (!string.IsNullOrWhiteSpace(DWINGS_GuaranteeID) && guaranteeCache != null)
                guaranteeCache.TryGetValue(DWINGS_GuaranteeID, out guarantee);

            PopulateInvoiceProperties(invoice);
            PopulateGuaranteeProperties(guarantee);
            NotifyAllDwingsProperties();
        }

        /// <summary>
        /// Fills the invoice-derived columns (<c>I_*</c>) from the given DTO.
        /// Safe with a null DTO — every column is cleared instead.
        /// <para>Called once during initial data load and again whenever <see cref="RefreshDwingsData"/> runs.</para>
        /// </summary>
        internal void PopulateInvoiceProperties(DwingsInvoiceDto invoice)
        {
            I_REQUESTED_INVOICE_AMOUNT  = invoice?.REQUESTED_AMOUNT?.ToString();
            I_SENDER_NAME               = invoice?.SENDER_NAME;
            I_RECEIVER_NAME             = invoice?.RECEIVER_NAME;
            I_SENDER_REFERENCE          = invoice?.SENDER_REFERENCE;
            I_RECEIVER_REFERENCE        = invoice?.RECEIVER_REFERENCE;
            I_T_INVOICE_STATUS          = invoice?.T_INVOICE_STATUS;
            _hasEmail                   = invoice?.COMM_ID_EMAIL;
            I_BILLING_AMOUNT            = invoice?.BILLING_AMOUNT?.ToString();
            I_BILLING_CURRENCY          = invoice?.BILLING_CURRENCY;
            I_START_DATE                = invoice?.START_DATE?.ToString("yyyy-MM-dd");
            I_END_DATE                  = invoice?.END_DATE?.ToString("yyyy-MM-dd");
            I_FINAL_AMOUNT              = invoice?.FINAL_AMOUNT?.ToString();
            I_BUSINESS_CASE_REFERENCE   = invoice?.BUSINESS_CASE_REFERENCE;
            I_BUSINESS_CASE_ID          = invoice?.BUSINESS_CASE_ID;
            I_SENDER_ACCOUNT_NUMBER     = invoice?.SENDER_ACCOUNT_NUMBER;
            I_SENDER_ACCOUNT_BIC        = invoice?.SENDER_ACCOUNT_BIC;
            I_REQUESTED_AMOUNT          = invoice?.REQUESTED_AMOUNT?.ToString();
            I_REQUESTED_EXECUTION_DATE  = invoice?.REQUESTED_EXECUTION_DATE?.ToString("yyyy-MM-dd");
            I_T_PAYMENT_REQUEST_STATUS  = invoice?.T_PAYMENT_REQUEST_STATUS;
            I_BGPMT                     = invoice?.BGPMT;
            I_MT_STATUS                 = invoice?.MT_STATUS;
            I_ERROR_MESSAGE             = invoice?.ERROR_MESSAGE;
            I_PAYMENT_METHOD            = invoice?.PAYMENT_METHOD;
            I_DEBTOR_PARTY_NAME         = invoice?.DEBTOR_PARTY_NAME;
        }

        /// <summary>
        /// Fills the guarantee-derived columns (<c>G_*</c>) from the given DTO.
        /// Also fills <c>I_RECEIVER_NAME</c> as a fallback from <c>NAME2</c> when the invoice DTO did not
        /// provide one — preserves legacy behaviour from the original inline implementation.
        /// </summary>
        internal void PopulateGuaranteeProperties(DwingsGuaranteeDto guarantee)
        {
            if (I_RECEIVER_NAME == null)
                I_RECEIVER_NAME = guarantee?.NAME2;

            G_GUARANTEE_TYPE                         = guarantee?.GUARANTEE_TYPE;
            G_NATURE                                 = guarantee?.NATURE;
            G_EVENT_STATUS                           = guarantee?.EVENT_STATUS;
            G_EVENT_EFFECTIVEDATE                    = guarantee?.EVENT_EFFECTIVEDATE?.ToString("yyyy-MM-dd");
            G_ISSUEDATE                              = guarantee?.ISSUEDATE?.ToString("yyyy-MM-dd");
            G_OFFICIALREF                            = guarantee?.OFFICIALREF ?? guarantee?.GUARANTEE_ID;
            G_UNDERTAKINGEVENT                       = guarantee?.UNDERTAKINGEVENT;
            G_PROCESS                                = guarantee?.PROCESS;
            G_EXPIRYDATETYPE                         = guarantee?.EXPIRYDATETYPE;
            G_EXPIRYDATE                             = guarantee?.EXPIRYDATE?.ToString("yyyy-MM-dd");
            G_PARTY_ID                               = guarantee?.PARTY_ID;
            G_PARTY_REF                              = guarantee?.PARTY_REF;
            G_SECONDARY_OBLIGOR                      = guarantee?.SECONDARY_OBLIGOR;
            G_SECONDARY_OBLIGOR_NATURE               = guarantee?.SECONDARY_OBLIGOR_NATURE;
            G_ROLE                                   = guarantee?.ROLE;
            G_COUNTRY                                = guarantee?.COUNTRY;
            G_CENTRAL_PARTY_CODE                     = guarantee?.CENTRAL_PARTY_CODE;
            G_NAME1                                  = guarantee?.NAME1;
            G_NAME2                                  = guarantee?.NAME2;
            G_GROUPE                                 = guarantee?.GROUPE;
            G_PREMIUM                                = guarantee?.PREMIUM?.ToString();
            G_BRANCH_CODE                            = guarantee?.BRANCH_CODE;
            G_BRANCH_NAME                            = guarantee?.BRANCH_NAME;
            G_OUTSTANDING_AMOUNT_IN_BOOKING_CURRENCY = guarantee?.OUTSTANDING_AMOUNT_IN_BOOKING_CURRENCY?.ToString();
            G_CANCELLATIONDATE                       = guarantee?.CANCELLATIONDATE?.ToString("yyyy-MM-dd");
            G_CONTROLER                              = guarantee?.CONTROLER;
            G_AUTOMATICBOOKOFF                       = guarantee?.AUTOMATICBOOKOFF;
            G_NATUREOFDEAL                           = guarantee?.NATUREOFDEAL;
        }

        /// <summary>
        /// Raises PropertyChanged for every DWINGS column in one burst so bound cells refresh together.
        /// Invoked after <see cref="PopulateInvoiceProperties"/>+<see cref="PopulateGuaranteeProperties"/>
        /// when the DTOs themselves changed (rather than individual setter-driven notifications).
        /// </summary>
        private void NotifyAllDwingsProperties()
        {
            // Invoice-derived columns
            OnPropertyChanged(nameof(I_REQUESTED_INVOICE_AMOUNT));
            OnPropertyChanged(nameof(I_SENDER_NAME));
            OnPropertyChanged(nameof(I_RECEIVER_NAME));
            OnPropertyChanged(nameof(I_SENDER_REFERENCE));
            OnPropertyChanged(nameof(I_RECEIVER_REFERENCE));
            OnPropertyChanged(nameof(I_T_INVOICE_STATUS));
            OnPropertyChanged(nameof(HasEmail));
            OnPropertyChanged(nameof(I_BILLING_AMOUNT));
            OnPropertyChanged(nameof(I_BILLING_CURRENCY));
            OnPropertyChanged(nameof(I_START_DATE));
            OnPropertyChanged(nameof(I_END_DATE));
            OnPropertyChanged(nameof(I_FINAL_AMOUNT));
            OnPropertyChanged(nameof(I_BUSINESS_CASE_REFERENCE));
            OnPropertyChanged(nameof(I_BUSINESS_CASE_ID));
            OnPropertyChanged(nameof(I_SENDER_ACCOUNT_NUMBER));
            OnPropertyChanged(nameof(I_SENDER_ACCOUNT_BIC));
            OnPropertyChanged(nameof(I_REQUESTED_AMOUNT));
            OnPropertyChanged(nameof(I_REQUESTED_EXECUTION_DATE));
            OnPropertyChanged(nameof(I_T_PAYMENT_REQUEST_STATUS));
            OnPropertyChanged(nameof(I_BGPMT));
            OnPropertyChanged(nameof(I_MT_STATUS));
            OnPropertyChanged(nameof(I_ERROR_MESSAGE));
            OnPropertyChanged(nameof(I_PAYMENT_METHOD));
            OnPropertyChanged(nameof(I_DEBTOR_PARTY_NAME));

            // Guarantee-derived columns
            OnPropertyChanged(nameof(G_GUARANTEE_TYPE));
            OnPropertyChanged(nameof(G_NATURE));
            OnPropertyChanged(nameof(G_EVENT_STATUS));
            OnPropertyChanged(nameof(G_EVENT_EFFECTIVEDATE));
            OnPropertyChanged(nameof(G_ISSUEDATE));
            OnPropertyChanged(nameof(G_OFFICIALREF));
            OnPropertyChanged(nameof(G_UNDERTAKINGEVENT));
            OnPropertyChanged(nameof(G_PROCESS));
            OnPropertyChanged(nameof(G_EXPIRYDATETYPE));
            OnPropertyChanged(nameof(G_EXPIRYDATE));
            OnPropertyChanged(nameof(G_PARTY_ID));
            OnPropertyChanged(nameof(G_PARTY_REF));
            OnPropertyChanged(nameof(G_SECONDARY_OBLIGOR));
            OnPropertyChanged(nameof(G_SECONDARY_OBLIGOR_NATURE));
            OnPropertyChanged(nameof(G_ROLE));
            OnPropertyChanged(nameof(G_COUNTRY));
            OnPropertyChanged(nameof(G_CENTRAL_PARTY_CODE));
            OnPropertyChanged(nameof(G_NAME1));
            OnPropertyChanged(nameof(G_NAME2));
            OnPropertyChanged(nameof(G_GROUPE));
            OnPropertyChanged(nameof(G_PREMIUM));
            OnPropertyChanged(nameof(G_BRANCH_CODE));
            OnPropertyChanged(nameof(G_BRANCH_NAME));
            OnPropertyChanged(nameof(G_OUTSTANDING_AMOUNT_IN_BOOKING_CURRENCY));
            OnPropertyChanged(nameof(G_CANCELLATIONDATE));
            OnPropertyChanged(nameof(G_CONTROLER));
            OnPropertyChanged(nameof(G_AUTOMATICBOOKOFF));
            OnPropertyChanged(nameof(G_NATUREOFDEAL));
        }
    }
}
