using System;
using System.IO;

namespace RecoTool.Services
{
    // Partial: connection string builders.
    // Centralizes all "give me a connection string for X" methods so callers don't have to
    // know about the underlying ACE driver flags or path resolution rules.
    // Path/ACE primitives (AceConn, GetLocal*Path, GetCountryDatabaseDirectory) live in
    // OfflineFirstService.Paths.cs; this partial only assembles them into connection strings.
    public partial class OfflineFirstService
    {
        /// <summary>
        /// Returns the Control DB connection string. Control DB is MANDATORY for KPI snapshots and sync metadata.
        /// Tries the explicit single ControlDatabasePath first, then per-country Control DB built from
        /// ControlDatabaseDirectory/ControlDatabasePrefix (with Country* fallbacks). Throws if not resolvable.
        /// </summary>
        public string GetControlConnectionString(string countryId = null)
        {
            // Control DB uses the same Access file as the global lock database
            var cid = countryId ?? CurrentCountryId;
            return GetRemoteLockConnectionString(cid);
        }

        /// <summary>
        /// Builds the country-specific Control DB path using CountryDatabaseDirectory and an optional ControlDatabasePrefix
        /// (falls back to CountryDatabasePrefix). Returns null if not enough info to construct.
        /// </summary>
        private string GetControlDbPath(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return null;

            // Directory always comes from CountryDatabaseDirectory (single place)
            string dir = GetCentralConfig("CountryDatabaseDirectory");
            if (string.IsNullOrWhiteSpace(dir))
                dir = GetParameter("CountryDatabaseDirectory");
            if (string.IsNullOrWhiteSpace(dir)) return null;

            string prefix = GetCentralConfig("ControlDatabasePrefix");
            if (string.IsNullOrWhiteSpace(prefix))
                prefix = GetCentralConfig("CountryDatabasePrefix");
            if (string.IsNullOrWhiteSpace(prefix))
                prefix = GetParameter("CountryDatabasePrefix") ?? "DB_";

            string file = $"{prefix}{countryId}_lock.accdb";
            return Path.Combine(dir, file);
        }

        /// <summary>
        /// Chaîne de connexion vers la base locale d'un pays donné.
        /// Ne nécessite pas que le pays soit le courant.
        /// </summary>
        public string GetCountryConnectionString(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));
            string dataDirectory = GetParameter("DataDirectory");
            string countryPrefix = GetParameter("CountryDatabasePrefix") ?? "DB_";
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new InvalidOperationException("Paramètre DataDirectory manquant (T_Param)");
            string localDbPath = Path.Combine(dataDirectory, $"{countryPrefix}{countryId}.accdb");
            return AceConn(localDbPath);
        }

        private string GetLocalConnectionString()
        {
            if (_syncConfig == null || string.IsNullOrWhiteSpace(_syncConfig.LocalDatabasePath))
                throw new InvalidOperationException("Configuration locale invalide");
            return AceConn(_syncConfig.LocalDatabasePath);
        }

        /// <summary>
        /// Expose publiquement la chaîne de connexion locale courante (lecture seule)
        /// </summary>
        public string GetCurrentLocalConnectionString()
        {
            EnsureInitialized();
            return GetLocalConnectionString();
        }
    }
}
