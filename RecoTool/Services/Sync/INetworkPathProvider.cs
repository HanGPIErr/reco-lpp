namespace RecoTool.Services.Sync
{
    /// <summary>
    /// Resolves network paths (UNC) for sync-related lookups. Centralises the
    /// path-building logic so consumers (today: <see cref="RecoTool.Infrastructure.Health.Checks.NetworkShareHealthCheck"/>)
    /// don't have to know about parameters, country prefixes, etc. — they just
    /// ask "give me the network ZIP for FR" or "is the share reachable?".
    ///
    /// <para>
    /// Production binding delegates to <see cref="OfflineFirstService"/>'s existing
    /// path resolution (via the parameters table). Tests inject a fake.
    /// </para>
    /// </summary>
    public interface INetworkPathProvider
    {
        /// <summary>UNC path to the read-only AMBRE zip on the network.</summary>
        string GetNetworkAmbreZipPath(string countryId);

        /// <summary>UNC path to the read-write country reconciliation database (.accdb).</summary>
        string GetNetworkReconciliationDbPath(string countryId);

        /// <summary>UNC path to the shared referential database.</summary>
        string GetReferentialNetworkPath();

        /// <summary>UNC path to the read-only DWINGS zip on the network.</summary>
        string GetNetworkDwZipPath(string countryId);

        /// <summary>
        /// True when the configured network root is reachable. Implementations should
        /// answer cheaply (file-share probe with short timeout) — UI calls this on
        /// every status refresh.
        /// </summary>
        bool IsNetworkAvailable();
    }
}
