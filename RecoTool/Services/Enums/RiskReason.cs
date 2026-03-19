using System.ComponentModel;

namespace RecoTool.Services
{
    /// <summary>
    /// Risk reasons (RISKY). Numeric IDs should match the referential if available.
    /// If your database defines specific IDs, adjust the values accordingly.
    /// </summary>
    public enum Risky
    {
        [Description("Commissions already collected and credit in the account 67P")] CollectedCommissionsCredit67P = 30,
        [Description("Fees not yet invoiced")] FeesNotYetInvoiced = 31,
        [Description("We do not observe risk of non payment for this client; expected payment delay")] NoObservedRiskExpectedDelay = 32,
        [Description("NOT BELONGING TO TFSC")] NotTFSC = 33
    }
}
