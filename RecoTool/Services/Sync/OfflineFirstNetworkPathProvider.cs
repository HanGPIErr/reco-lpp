using System;
using System.IO;

namespace RecoTool.Services.Sync
{
    /// <summary>
    /// Adapter that satisfies <see cref="INetworkPathProvider"/> by reading the
    /// legacy <see cref="OfflineFirstService"/>'s parameter table. Used during
    /// the Lot 5 transition: V2 components depend on <see cref="INetworkPathProvider"/>
    /// while paths still come from the existing OFS configuration.
    ///
    /// <para>
    /// Once OFS V2 is fully wired and the parameter table moves to its own
    /// dedicated abstraction (<c>IParameterStore</c>), this adapter can be
    /// retired in favor of a stand-alone <c>ConfigDrivenNetworkPathProvider</c>.
    /// </para>
    ///
    /// <para>
    /// Note : we re-implement path resolution here (instead of delegating to
    /// private OFS helpers) to avoid changing OFS visibility. The logic mirrors
    /// <c>OfflineFirstService.Paths.cs</c> — keep the two in sync until the
    /// legacy methods are removed.
    /// </para>
    /// </summary>
    public sealed class OfflineFirstNetworkPathProvider : INetworkPathProvider
    {
        private readonly OfflineFirstService _offline;

        public OfflineFirstNetworkPathProvider(OfflineFirstService offline)
        {
            _offline = offline ?? throw new ArgumentNullException(nameof(offline));
        }

        public string GetNetworkAmbreZipPath(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return null;
            try
            {
                var remoteDir = _offline.GetParameter("CountryDatabaseDirectory");
                var prefix = _offline.GetParameter("AmbreDatabasePrefix")
                          ?? _offline.GetParameter("CountryDatabasePrefix")
                          ?? "DB_";
                if (string.IsNullOrWhiteSpace(remoteDir)) return null;
                return Path.Combine(remoteDir, $"{prefix}{countryId}.zip");
            }
            catch { return null; }
        }

        public string GetNetworkReconciliationDbPath(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return null;
            try
            {
                var remoteDir = _offline.GetParameter("CountryDatabaseDirectory");
                var prefix = _offline.GetParameter("CountryDatabasePrefix") ?? "DB_";
                if (string.IsNullOrWhiteSpace(remoteDir)) return null;
                return Path.Combine(remoteDir, $"{prefix}{countryId}.accdb");
            }
            catch { return null; }
        }

        public string GetReferentialNetworkPath()
        {
            try { return _offline.ReferentialDatabasePath; }
            catch { return null; }
        }

        public string GetNetworkDwZipPath(string countryId)
        {
            try { return _offline.GetNetworkDwZipPath(countryId); }
            catch { return null; }
        }

        public bool IsNetworkAvailable()
        {
            try { return _offline.IsNetworkSyncAvailable; }
            catch { return false; }
        }
    }
}
