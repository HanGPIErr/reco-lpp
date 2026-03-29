using RecoTool.Models;
using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.OleDb;
using System.IO;

namespace RecoTool.Services
{
    // Partial: country context, initialization flags, availability checks
    public partial class OfflineFirstService
    {
        private bool _isInitialized = false;
        private bool _disposed = false;

        // Persistent network connection – kept open while a country is selected to avoid
        // repeated Open/Close (and .ldb create/delete) on every push/pull over slow networks.
        private OleDbConnection _persistentNetworkConn;
        private readonly object _netConnLock = new object();

        /// <summary>
        /// Returns the persistent network OleDbConnection, opening it if necessary.
        /// Thread-safe. The connection is kept alive for the lifetime of the current country.
        /// </summary>
        internal OleDbConnection GetOrOpenNetworkConnection()
        {
            lock (_netConnLock)
            {
                if (_persistentNetworkConn != null && _persistentNetworkConn.State == ConnectionState.Open)
                    return _persistentNetworkConn;

                // Close stale connection if any
                CloseNetworkConnectionInternal();

                if (string.IsNullOrWhiteSpace(_currentCountryId)) return null;

                try
                {
                    var cs = GetNetworkCountryConnectionString(_currentCountryId);
                    _persistentNetworkConn = new OleDbConnection(cs);
                    _persistentNetworkConn.Open();
                    System.Diagnostics.Debug.WriteLine($"[NetConn] Persistent network connection opened for {_currentCountryId}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NetConn] Failed to open persistent connection: {ex.Message}");
                    _persistentNetworkConn?.Dispose();
                    _persistentNetworkConn = null;
                }
                return _persistentNetworkConn;
            }
        }

        /// <summary>
        /// Returns the persistent network connection asynchronously (opens on background thread if needed).
        /// </summary>
        internal async System.Threading.Tasks.Task<OleDbConnection> GetOrOpenNetworkConnectionAsync(System.Threading.CancellationToken token = default)
        {
            OleDbConnection conn;
            lock (_netConnLock)
            {
                if (_persistentNetworkConn != null && _persistentNetworkConn.State == ConnectionState.Open)
                    return _persistentNetworkConn;

                CloseNetworkConnectionInternal();

                if (string.IsNullOrWhiteSpace(_currentCountryId)) return null;

                var cs = GetNetworkCountryConnectionString(_currentCountryId);
                _persistentNetworkConn = new OleDbConnection(cs);
                conn = _persistentNetworkConn;
            }

            try
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[NetConn] Persistent network connection opened (async) for {_currentCountryId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NetConn] Failed to open persistent connection (async): {ex.Message}");
                lock (_netConnLock)
                {
                    _persistentNetworkConn?.Dispose();
                    _persistentNetworkConn = null;
                }
                return null;
            }
            return conn;
        }

        /// <summary>
        /// Closes and disposes the persistent network connection. Called on country change and Dispose.
        /// </summary>
        internal void CloseNetworkConnection()
        {
            lock (_netConnLock) { CloseNetworkConnectionInternal(); }
        }

        private void CloseNetworkConnectionInternal()
        {
            if (_persistentNetworkConn != null)
            {
                try { _persistentNetworkConn.Close(); } catch { }
                try { _persistentNetworkConn.Dispose(); } catch { }
                _persistentNetworkConn = null;
                System.Diagnostics.Debug.WriteLine("[NetConn] Persistent network connection closed.");
            }
        }

        // Gestion d'une seule country à la fois
        private Country _currentCountry;
        private string _currentCountryId;
        private readonly string _currentUser;
        private readonly ConcurrentDictionary<string, object> _countrySyncLocks = new ConcurrentDictionary<string, object>();

        public string ReferentialDatabasePath => _ReferentialDatabasePath;

        // Prefer using this when opening OleDbConnection to the referential database
        public string ReferentialConnectionString => AceConn(_ReferentialDatabasePath);

        /// <summary>
        /// Utilisateur actuel pour le verrouillage des enregistrements
        /// </summary>
        public string CurrentUser => _currentUser;

        /// <summary>
        /// Indique si le service est initialisé
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Indique si la synchronisation réseau est disponible (basé sur la config et l'accès au fichier distant)
        /// </summary>
        public bool IsNetworkSyncAvailable
        {
            get
            {
                try
                {
                    var remotePath = _syncConfig?.RemoteDatabasePath;
                    return !string.IsNullOrWhiteSpace(remotePath) && File.Exists(remotePath);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Pays actuellement sélectionné (lecture seule)
        /// </summary>
        public Country CurrentCountry => _currentCountry;

        /// <summary>
        /// Identifiant du pays actuellement sélectionné (lecture seule)
        /// </summary>
        public string CurrentCountryId => _currentCountryId;
    }
}
