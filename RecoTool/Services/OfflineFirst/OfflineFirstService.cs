using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RecoTool.Models;
using OfflineFirstAccess.Models;
using OfflineFirstAccess.Helpers;
using System.Threading;
using System.Collections.Concurrent;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.IO.Compression;
using System.Timers;
using System.Text;
using System.Globalization;
using RecoTool.Helpers;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.Infrastructure.Time;
using RecoTool.Services.Helpers;
using System.Diagnostics;

namespace RecoTool.Services
{
    /// <summary>
    /// Service de gestion des accès offline-first aux bases de données Access
    /// Gère deux types de bases :
    /// - Référentielles : chargées en mémoire une seule fois (lecture seule)
    /// - Par country : synchronisation offline-first avec OfflineFirstAccess.dll
    ///
    /// Other concerns are split into sibling partials: see
    /// <c>OfflineFirstService.ConnectionStrings.cs</c>, <c>OfflineFirstService.Paths.cs</c>,
    /// <c>OfflineFirstService.Configuration.cs</c>, <c>OfflineFirstService.CountryContext.cs</c>,
    /// <c>OfflineFirstService.Locks.cs</c>, <c>OfflineFirstService.GlobalLock.cs</c>,
    /// <c>OfflineFirstService.ChangeLog.cs</c>, <c>OfflineFirstService.Schema.cs</c>,
    /// <c>OfflineFirstService.Snapshots.cs</c>, <c>OfflineFirstService.Push.cs</c>,
    /// <c>OfflineFirstService.IO.cs</c>, <c>OfflineFirstService.Diagnostics.cs</c>,
    /// <c>OfflineFirstService.Referentials.cs</c>, <c>OfflineFirstService.Events.cs</c>,
    /// <c>OfflineFirstService.Maintenance.cs</c>, <c>OfflineFirstService.SyncGates.cs</c>,
    /// <c>OfflineFirstService.SyncOperations.cs</c>, <c>OfflineFirstService.BatchOps.cs</c>,
    /// <c>OfflineFirstService.Replication.cs</c>, <c>OfflineFirstService.Crc.cs</c>.
    /// </summary>
    public partial class OfflineFirstService : IDisposable, IOfflineFirstService
    {
        // Controls whether background pushes are allowed by SyncMonitorService and other periodic mechanisms
        public bool AllowBackgroundPushes { get; set; } = true;

        // Abstraction over DateTime.Now/UtcNow/Today so time-dependent logic can be unit-tested.
        // Defaults to SystemClock.Instance when not provided (production path); DI injects the
        // singleton registered in App.xaml.cs. All partials of this class share this field.
        private readonly IClock _clock;

        // Ensure configuration is loaded as soon as the service is constructed so that
        // referential connection string is available at startup before any LoadReferentialsAsync calls
        public OfflineFirstService(IClock clock = null)
        {
            _clock = clock ?? SystemClock.Instance;
            try
            {
                LoadConfiguration();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Startup] LoadConfiguration failed: {ex.Message}");
                throw;
            }
        }

        #region Cache mémoire pour les référentiels

        // Cache singleton pour les référentiels (partagé entre toutes les instances)
        private static readonly object _referentialLock = new object();
        private static bool _referentialsLoaded = false;
        private static DateTime _referentialsLoadTime;

        // Collections en mémoire pour les tables référentielles
        private static List<AmbreImportField> _ambreImportFields = new List<AmbreImportField>();
        private static List<AmbreTransactionCode> _ambreTransactionCodes = new List<AmbreTransactionCode>();
        private static List<AmbreTransform> _ambreTransforms = new List<AmbreTransform>();
        private static List<Country> _countries = new List<Country>();
        private static List<UserField> _userFields = new List<UserField>();
        private static List<UserFilter> _userFilters = new List<UserFilter>();
        private static List<Param> _params = new List<Param>();

        #endregion

        #region Sync State Notifications

        // Ambre import scope counter (supports nested scopes)
        private int _ambreImportScopeCount;

        public IDisposable BeginAmbreImportScope()
        {
            Interlocked.Increment(ref _ambreImportScopeCount);
            return new AmbreImportScope(this);
        }

        /// <summary>
        /// Returns true if an Ambre import is currently in progress
        /// </summary>
        public bool IsAmbreImportInProgress()
        {
            return Interlocked.CompareExchange(ref _ambreImportScopeCount, 0, 0) > 0;
        }

        private sealed class AmbreImportScope : IDisposable
        {
            private OfflineFirstService _svc;
            private int _disposed;
            public AmbreImportScope(OfflineFirstService svc) { _svc = svc; }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                if (_svc != null)
                {
                    Interlocked.Decrement(ref _svc._ambreImportScopeCount);
                    _svc = null;
                }
            }
        }

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.GlobalLock.cs
           Contains: IsGlobalLockActiveByOthersAsync, NoopLockHandle */

        #endregion

        #region Background Push Control
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.Push.cs */
        #endregion

        #region Per-country Sync Gate
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.SyncGates.cs */
        #endregion

        #region Background Sync Scheduler
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.SyncGates.cs (ScheduleSyncIfNeededAsync) */
        #endregion

        #region Lock Helpers

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.BatchOps.cs (IsAccessLockException) */

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.SyncOperations.cs (PushReconciliationIfPendingAsync) */

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.ConnectionStrings.cs
           Contains: GetControlConnectionString, GetControlDbPath */

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.Replication.cs
           Contains: ExtractAmbreZipToLocalAsync, CopyZipIfDifferentAsync, FilesAreEqual,
                     ExtractDwZipToLocalAsync, SetLastSyncAnchorAsync, IsDatabaseLockedAsync,
                     IsLocalReconciliationEmptyAsync, CopyLocalToNetworkAsync,
                     CreateLocalReconciliationBackupAsync, MarkAllLocalChangesAsSyncedAsync,
                     CopyNetworkToLocalAsync, CopyNetworkToLocalAmbreAsync,
                     CopyNetworkToLocalReconciliationAsync, CopyNetworkToLocalDwAsync,
                     CopyLocalToNetworkAmbreAsync, CopyLocalToNetworkReconciliationAsync */

        /// <summary>
        /// Retourne le chemin du ZIP DW réseau le plus pertinent pour un pays (le plus récent contenant le pays et "DW/DWINGS"). Peut renvoyer null.
        /// </summary>
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.Paths.cs (GetNetworkDwZipPath) */

        /// <summary>
        /// Retourne le chemin du cache local ZIP DW (nom stable).
        /// </summary>
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.Paths.cs (GetLocalDwZipCachePath) */

        /// <summary>
        /// Vérifie si le ZIP DW local correspond au ZIP DW réseau (taille/contenu). True si pas de ZIP réseau ou si identiques.
        /// </summary>
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.Paths.cs (IsLocalDwZipInSyncWithNetworkAsync) */
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.Diagnostics.cs (GetAmbreZipDiagnostics, GetDwZipDiagnostics) */

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.ConnectionStrings.cs (GetCountryConnectionString) */

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.BatchOps.cs (ApplyEntitiesBatchAsync, DeleteEntityAsync) */

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.GlobalLock.cs
           Contains: GlobalLockHandle, ProcessGateLockHandle, LogOleDbCommand,
                     AcquireGlobalLockInternalAsync, ForceBreakGlobalLockAsync */

        #endregion

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.GlobalLock.cs
           Contains: IsGlobalLockActiveAsync, WaitForGlobalLockReleaseAsync, SetSyncStatusAsync, GetCurrentSyncStatusAsync */

        #region Configuration Properties

        // Configuration centralisée dans T_Param - plus de propriétés redondantes
        // Utilisation directe de GetParameter() pour tous les paramètres applicatifs

        #endregion

        #region Fields and Properties

        private string _ReferentialDatabasePath;

        // Sync configuration (column names, paths) — used by entity processing (import/archive)
        // Note: SynchronizationService removed — sync is handled directly by PushReconciliationIfPendingAsync/PullReconciliationFromNetworkAsync
        private SyncConfiguration _syncConfig;
        private readonly ConcurrentDictionary<string, DateTime> _lastSyncTimes =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lockObject = new object();
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.GlobalLock.cs (_acquireGlobalProcessGate, _processLockHeld) */

        /// <summary>
        /// Returns true if a synchronization is currently in progress for the specified country.
        /// This uses the internal semaphore to detect if the sync lock is held.
        /// </summary>
        public bool IsSynchronizationInProgress(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return false;
            var sem = _syncSemaphores.GetOrAdd(countryId, _ => new SemaphoreSlim(1, 1));
            // Try to acquire without waiting: if we can, then no sync is currently running.
            if (sem.Wait(0))
            {
                sem.Release();
                return false;
            }
            return true;
        }

        /// <summary>
        /// Wait until the current synchronization (if any) completes for the specified country.
        /// Returns immediately if no synchronization is running.
        /// </summary>
        public async Task WaitForSynchronizationAsync(string countryId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return;
            var sem = _syncSemaphores.GetOrAdd(countryId, _ => new SemaphoreSlim(1, 1));
            // Wait to acquire; if already free, this returns immediately. Then release so others can proceed.
            await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
            sem.Release();
        }
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.CountryContext.cs (state fields and properties) */

        /// <summary>
        /// Liste des pays depuis les référentiels (copie pour immutabilité côté appelant)
        /// </summary>
        public List<Country> Countries
        {
            get
            {
                lock (_referentialLock)
                {
                    return new List<Country>(_countries);
                }
            }
        }

        #endregion

        #region Constructor


        /// <summary>
        /// Charge toutes les tables référentielles en mémoire une seule fois, de manière thread-safe.
        /// </summary>
        public async Task LoadReferentialsAsync()
        {
            // Defensive: if configuration hasn't been loaded yet, load it now
            if (string.IsNullOrWhiteSpace(_ReferentialDatabasePath))
            {
                LoadConfiguration();
            }

            // Double-checked locking pour éviter les rechargements inutiles
            if (_referentialsLoaded) return;

            List<AmbreImportField> ambreImportFields = new List<AmbreImportField>();
            List<AmbreTransactionCode> ambreTransactionCodes = new List<AmbreTransactionCode>();
            List<AmbreTransform> ambreTransforms = new List<AmbreTransform>();
            List<Country> countries = new List<Country>();
            List<UserField> userFields = new List<UserField>();
            List<UserFilter> userFilters = new List<UserFilter>();
            List<Param> parameters = new List<Param>();

            try
            {
                // Initialize the referential connection pool (singleton, idempotent)
                Infrastructure.DataAccess.ReferentialConnectionPool.Initialize(ReferentialConnectionString);
                var connection = await Infrastructure.DataAccess.ReferentialConnectionPool.Instance
                    .GetConnectionAsync().ConfigureAwait(false);

                {
                    // Helpers locaux
                    async Task LoadListAsync<T>(string sql, Func<IDataReader, T> map, List<T> target)
                    {
                        using (var cmd = new OleDbCommand(sql, connection))
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                                target.Add(map(reader));
                        }
                    }

                    await LoadListAsync(
                        "SELECT AMB_Source, AMB_Destination FROM T_Ref_Ambre_ImportFields ORDER BY AMB_Source",
                        r => new AmbreImportField
                        {
                            AMB_Source = r["AMB_Source"]?.ToString(),
                            AMB_Destination = r["AMB_Destination"]?.ToString()
                        },
                        ambreImportFields);

                    await LoadListAsync(
                        "SELECT ATC_ID, ATC_CODE, ATC_TAG FROM T_Ref_Ambre_TransactionCodes ORDER BY ATC_ID",
                        r => new AmbreTransactionCode
                        {
                            ATC_ID = Convert.ToInt32(r["ATC_ID"]),
                            ATC_CODE = r["ATC_CODE"]?.ToString(),
                            ATC_TAG = r["ATC_TAG"]?.ToString()
                        },
                        ambreTransactionCodes);

                    await LoadListAsync(
                        "SELECT AMB_Source, AMB_Destination, AMB_TransformationFunction, AMB_Description FROM T_Ref_Ambre_Transform",
                        r => new AmbreTransform
                        {
                            AMB_Source = r["AMB_Source"]?.ToString(),
                            AMB_Destination = r["AMB_Destination"]?.ToString(),
                            AMB_TransformationFunction = r["AMB_TransformationFunction"]?.ToString(),
                            AMB_Description = r["AMB_Description"]?.ToString()
                        },
                        ambreTransforms);

                    await LoadListAsync(
                        "SELECT CNT_Id, CNT_Name, CNT_AmbrePivotCountryId, CNT_AmbrePivot, CNT_AmbreReceivable, CNT_AmbreReceivableCountryId, CNT_ServiceCode, CNT_BIC, CNT_DWID FROM T_Ref_Country ORDER BY CNT_Name",
                        r => new Country
                        {
                            CNT_Id = r["CNT_Id"]?.ToString(),
                            CNT_Name = r["CNT_Name"]?.ToString(),
                            CNT_AmbrePivotCountryId = SafeInt(r["CNT_AmbrePivotCountryId"]),
                            CNT_AmbrePivot = r["CNT_AmbrePivot"]?.ToString(),
                            CNT_AmbreReceivable = r["CNT_AmbreReceivable"]?.ToString(),
                            CNT_AmbreReceivableCountryId = SafeInt(r["CNT_AmbreReceivableCountryId"]),
                            CNT_ServiceCode = r["CNT_ServiceCode"]?.ToString(),
                            CNT_BIC = r["CNT_BIC"]?.ToString(),
                            CNT_DWID = r["CNT_DWID"]?.ToString()
                        },
                        countries);

                    await LoadListAsync(
                        "SELECT USR_ID, USR_Category, USR_FieldName, USR_FieldDescription, USR_Pivot, USR_Receivable, USR_Color FROM T_Ref_User_Fields ORDER BY USR_Category, USR_FieldName",
                        r => new UserField
                        {
                            USR_ID = SafeInt(r["USR_ID"]),
                            USR_Category = r["USR_Category"]?.ToString(),
                            USR_FieldName = r["USR_FieldName"]?.ToString(),
                            USR_FieldDescription = r["USR_FieldDescription"]?.ToString(),
                            USR_Pivot = SafeBool(r["USR_Pivot"]),
                            USR_Receivable = SafeBool(r["USR_Receivable"]),
                            USR_Color = r["USR_Color"]?.ToString()
                        },
                        userFields);

                    await LoadListAsync(
                        "SELECT UFI_id, UFI_Name, UFI_SQL, UFI_CreatedBy FROM T_Ref_User_Filter ORDER BY UFI_Name",
                        r => new UserFilter
                        {
                            UFI_id = SafeInt(r["UFI_id"]),
                            UFI_Name = r["UFI_Name"]?.ToString(),
                            UFI_SQL = r["UFI_SQL"]?.ToString(),
                            UFI_CreatedBy = r["UFI_CreatedBy"]?.ToString()
                        },
                        userFilters);

                    await LoadListAsync(
                        "SELECT PAR_Key, PAR_Value, PAR_Description FROM T_Param",
                        r => new Param
                        {
                            PAR_Key = r["PAR_Key"]?.ToString(),
                            PAR_Value = r["PAR_Value"]?.ToString(),
                            PAR_Description = r["PAR_Description"]?.ToString()
                        },
                        parameters);
                }

                lock (_referentialLock)
                {
                    if (_referentialsLoaded) return; // quelqu'un a déjà chargé
                    _ambreImportFields = ambreImportFields;
                    _ambreTransactionCodes = ambreTransactionCodes;
                    _ambreTransforms = ambreTransforms;
                    _countries = countries;
                    _userFields = userFields;
                    _userFilters = userFilters;
                    _params = parameters;
                    _referentialsLoaded = true;
                    _referentialsLoadTime = _clock.UtcNow;
                }

                // Initialiser des propriétés dépendantes des paramètres (ajoute valeurs défaut si nécessaires)
                InitializePropertiesFromParams();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[REF LOAD] Erreur lors du chargement des référentiels: {ex.Message}");
                throw;
            }

            // Helpers locaux pour mapping
            static int SafeInt(object o)
            {
                try { return o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o); } catch { return 0; }
            }
            static bool SafeBool(object o)
            {
                try { return o != null && o != DBNull.Value && Convert.ToBoolean(o); } catch { return false; }
            }
        }


        // Ensure the dedicated local ChangeLog database exists and has the proper schema
        private async Task EnsureLocalChangeLogSchemaAsync(string countryId)
        {
            if (!_useLocalChangeLog) return;
            if (string.IsNullOrWhiteSpace(countryId)) return;
            try
            {
                var changeLogDbPath = GetLocalChangeLogDbPath(countryId);

                // Ensure directory exists
                var dir = Path.GetDirectoryName(changeLogDbPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // If DB missing, create with a minimal schema containing only ChangeLog
                if (!File.Exists(changeLogDbPath))
                {
                    await DatabaseTemplateGenerator.CreateCustomTemplateAsync(changeLogDbPath, config =>
                    {
                        config.Tables.Clear();
                        var changeLogTable = new TableConfiguration
                        {
                            Name = "ChangeLog",
                            PrimaryKeyColumn = "ChangeID",
                            PrimaryKeyType = typeof(long),
                            LastModifiedColumn = null,
                            CreateTableSql = @"CREATE TABLE [ChangeLog] (
    [ChangeID] COUNTER PRIMARY KEY,
    [TableName] TEXT(128) NOT NULL,
    [RecordID] TEXT(255),
    [Operation] TEXT(255) NOT NULL,
    [Timestamp] DATETIME NOT NULL,
    [Synchronized] BIT NOT NULL
)"
                        };
                        changeLogTable.Columns.Add(new ColumnDefinition("ChangeID", typeof(long), "LONG", false, true, true));
                        changeLogTable.Columns.Add(new ColumnDefinition("TableName", typeof(string), "TEXT(128)", false));
                        changeLogTable.Columns.Add(new ColumnDefinition("RecordID", typeof(string), "TEXT(255)", true));
                        changeLogTable.Columns.Add(new ColumnDefinition("Operation", typeof(string), "TEXT(255)", false));
                        changeLogTable.Columns.Add(new ColumnDefinition("Timestamp", typeof(DateTime), "DATETIME", false));
                        changeLogTable.Columns.Add(new ColumnDefinition("Synchronized", typeof(bool), "BIT", false));
                        config.Tables.Add(changeLogTable);
                    });
                }

                // Open the dedicated ChangeLog DB and ensure columns exist (best-effort schema repair)
                using (var connection = new OleDbConnection(AceConn(changeLogDbPath)))
                {
                    await connection.OpenAsync();

                    // Check if ChangeLog table exists
                    bool tableExists = false;
                    using (var tblSchema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, null, "TABLE" }))
                    {
                        if (tblSchema != null)
                        {
                            foreach (System.Data.DataRow row in tblSchema.Rows)
                            {
                                var name = row["TABLE_NAME"]?.ToString();
                                if (string.Equals(name, "ChangeLog", StringComparison.OrdinalIgnoreCase))
                                {
                                    tableExists = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!tableExists)
                    {
                        var createSql = @"CREATE TABLE [ChangeLog] (
    [ChangeID] COUNTER PRIMARY KEY,
    [TableName] TEXT(128) NOT NULL,
    [RecordID] TEXT(255),
    [Operation] TEXT(255) NOT NULL,
    [Timestamp] DATETIME NOT NULL,
    [Synchronized] BIT NOT NULL
)";
                        using (var cmd = new OleDbCommand(createSql, connection))
                        {
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    // Ensure required columns exist (best-effort). No type migrations; deployment guarantees fresh DBs.
                    var requiredCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "ChangeID", "COUNTER" },
                        { "TableName", "TEXT(128)" },
                        { "RecordID", "TEXT(255)" },
                        { "Operation", "TEXT(255)" },
                        { "Timestamp", "DATETIME" },
                        { "Synchronized", "BIT" }
                    };

                    var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var colSchema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, new object[] { null, null, "ChangeLog", null }))
                    {
                        if (colSchema != null)
                        {
                            foreach (System.Data.DataRow row in colSchema.Rows)
                            {
                                var colName = row["COLUMN_NAME"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(colName))
                                {
                                    existingCols.Add(colName);
                                }
                            }
                        }
                    }

                    foreach (var kv in requiredCols)
                    {
                        if (!existingCols.Contains(kv.Key))
                        {
                            var alter = $"ALTER TABLE [ChangeLog] ADD COLUMN [{kv.Key}] {kv.Value}";
                            try { using (var cmd = new OleDbCommand(alter, connection)) { await cmd.ExecuteNonQueryAsync(); } }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MIGRATION] EnsureLocalChangeLogSchemaAsync failed for {countryId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens a change-log session against the remote lock database for the specified country.
        /// Call CommitAsync() to commit, otherwise Dispose() will rollback.
        /// </summary>
        public async Task<OfflineFirstAccess.ChangeTracking.IChangeLogSession> BeginChangeLogSessionAsync(string countryId)
        {
            // S'assurer que le schéma ChangeLog local existe et que Timestamp est DATETIME (migration si besoin)
            try { await EnsureLocalChangeLogSchemaAsync(countryId); } catch { /* best-effort */ }
            var tracker = new OfflineFirstAccess.ChangeTracking.ChangeTracker(GetChangeLogConnectionString(countryId));
            return await tracker.BeginSessionAsync();
        }

        /// <summary>
        /// Returns the number of unsynchronized change-log entries for the specified country
        /// from the remote lock database (where ChangeLog resides).
        /// </summary>
        public async Task<int> GetUnsyncedChangeCountAsync(string countryId)
        {
            // Pas besoin d'initialisation complète: ne dépend que de la base de lock distante
            var tracker = new OfflineFirstAccess.ChangeTracking.ChangeTracker(GetChangeLogConnectionString(countryId));
            var entries = await tracker.GetUnsyncedChangesAsync();
            return entries?.Count() ?? 0;
        }

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.GlobalLock.cs (AcquireGlobalLockAsync) */

        private void EnsureInitialized()
        {
            if (!_isInitialized || _syncConfig == null)
                throw new InvalidOperationException("OfflineFirstService non initialisé. Appelez SetCurrentCountryAsync d'abord.");
        }

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.ConnectionStrings.cs
           Contains: GetLocalConnectionString, GetCurrentLocalConnectionString */

        private async Task<HashSet<string>> GetTableColumnsAsync(OleDbConnection connection, string tableName)
        {
            using (var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, new object[] { null, null, tableName, null }))
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (schema != null)
                {
                    foreach (System.Data.DataRow row in schema.Rows)
                    {
                        set.Add(row["COLUMN_NAME"].ToString());
                    }
                }
                return await Task.FromResult(set);
            }
        }


        private async Task<string> GetPrimaryKeyColumnAsync(OleDbConnection connection, string tableName)
        {
            // 1) Try explicit Primary_Keys schema
            using (var pkSchema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Primary_Keys, new object[] { null, null, tableName }))
            {
                if (pkSchema != null && pkSchema.Rows.Count > 0)
                {
                    string first = null;
                    foreach (System.Data.DataRow row in pkSchema.Rows)
                    {
                        var name = row["COLUMN_NAME"]?.ToString();
                        if (string.Equals(name, "ID", StringComparison.OrdinalIgnoreCase))
                            return "ID";
                        if (first == null && !string.IsNullOrWhiteSpace(name))
                            first = name;
                    }
                    if (!string.IsNullOrWhiteSpace(first)) return first;
                }
            }

            // 2) Fallback: check Indexes schema for PRIMARY_KEY
            using (var idxSchema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Indexes, new object[] { null, null, null, null, tableName }))
            {
                if (idxSchema != null)
                {
                    var rows = idxSchema.Select("PRIMARY_KEY = true");
                    if (rows != null && rows.Length > 0)
                    {
                        var name = rows[0]["COLUMN_NAME"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }

            // 3) Heuristics: prefer an ID column if present, else first column name
            var cols = await GetTableColumnsAsync(connection, tableName);
            if (cols.Contains("ID", StringComparer.OrdinalIgnoreCase)) return "ID";
            if (cols.Count > 0) return cols.First();
            return null;
        }

        #region Configuration Methods
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.Configuration.cs
           Contains: LoadConfiguration, GetParameter, GetCountries, InitializePropertiesFromParams, RefreshConfigurationAsync */
        #endregion

        #region Database Connection Methods

        /// <summary>
        /// Retourne le chemin local attendu pour la base DW (même nom de fichier que la base réseau, dans DataDirectory)
        /// </summary>
        public string GetLocalDWDatabasePath()
        {
            // Réutilise la logique unifiée: DW est par pays via CountryDatabaseDirectory + DWDatabasePrefix
            // Si aucun pays courant n'est défini, on ne peut pas résoudre le chemin local DW
            if (string.IsNullOrEmpty(_currentCountryId)) return null;
            try { return GetLocalDwDbPath(_currentCountryId); } catch { return null; }
        }

        /// <returns>SyncConfiguration prête pour SynchronizationService</returns>
        private SyncConfiguration BuildSyncConfiguration(string countryId)
        {
            // Récupérer chemins et préfixe
            string dataDirectory = GetParameter("DataDirectory");
            string countryPrefix = GetParameter("CountryDatabasePrefix") ?? "DB_";
            string remoteDir = GetParameter("CountryDatabaseDirectory");

            // Construire chemins local et distant
            string localDbPath = Path.Combine(dataDirectory, $"{countryPrefix}{countryId}.accdb");
            string remoteDbPath = Path.Combine(remoteDir, $"{countryPrefix}{countryId}.accdb");

            // Tables à synchroniser (depuis T_Param, sinon défaut)
            var tables = new List<string>();
            string syncTables = GetParameter("SyncTables");
            if (!string.IsNullOrWhiteSpace(syncTables))
            {
                foreach (var t in syncTables.Split(','))
                {
                    var name = t?.Trim();
                    if (!string.IsNullOrEmpty(name)) tables.Add(name);
                }
            }
            if (tables.Count == 0)
            {
                tables.Add("T_Reconciliation");
            }

            // Dynamic toggle from T_Param: EnableSyncLog or SYNCLOG (true/false)
            bool enableSyncLog = false;
            try
            {
                var p = GetParameter("EnableSyncLog");
                if (string.IsNullOrWhiteSpace(p)) p = GetParameter("SYNCLOG");
                if (!string.IsNullOrWhiteSpace(p)) bool.TryParse(p.Trim(), out enableSyncLog);
            }
            catch { }

            return new SyncConfiguration
            {
                LocalDatabasePath = localDbPath,
                RemoteDatabasePath = remoteDbPath,
                LockDatabasePath = Path.Combine(remoteDir, $"{countryPrefix}{countryId}_lock.accdb"),
                TablesToSync = tables,
                EnableSyncLog = enableSyncLog
            };
        }

        /// <summary>
        /// Construit une configuration de synchro pour la base RECONCILIATION (tables RECON uniquement)
        /// </summary>
        private SyncConfiguration BuildReconciliationSyncConfiguration(string countryId, List<string> reconTables)
        {
            if (reconTables == null) reconTables = new List<string>();
            if (reconTables.Count == 0) reconTables.Add("T_Reconciliation");

            string localDbPath = GetLocalReconciliationDbPath(countryId);
            string remoteDbPath = GetNetworkReconciliationDbPath(countryId);

            // Utilise le même lock DB par pays
            string remoteDir = Path.GetDirectoryName(remoteDbPath);
            string countryPrefix = GetParameter("CountryDatabasePrefix") ?? "DB_";

            bool enableSyncLogRecon = false;
            try
            {
                var p = GetParameter("EnableSyncLog");
                if (string.IsNullOrWhiteSpace(p)) p = GetParameter("SYNCLOG");
                if (!string.IsNullOrWhiteSpace(p)) bool.TryParse(p.Trim(), out enableSyncLogRecon);
            }
            catch { }

            return new SyncConfiguration
            {
                LocalDatabasePath = localDbPath,
                RemoteDatabasePath = remoteDbPath,
                LockDatabasePath = Path.Combine(remoteDir, $"{countryPrefix}{countryId}_lock.accdb"),
                ChangeLogConnectionString = GetChangeLogConnectionString(countryId),
                TablesToSync = reconTables,
                EnableSyncLog = enableSyncLogRecon
            };
        }

        /// <summary>
        /// S'assure que les tables de la base de lock existent avec la structure unifiée
        /// </summary>
        /// <param name="lockDbPath">Chemin de la base de lock</param>
        private void EnsureLockTablesExist(string lockDbPath)
        {
            try
            {
                using (var connection = new OleDbConnection(AceConn(lockDbPath)))
                {
                    connection.Open();

                    bool HasTable(string tableName)
                    {
                        DataTable schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, tableName, "TABLE" });
                        return schema != null && schema.Rows.Count > 0;
                    }

                    void Exec(string sql)
                    {
                        using (var cmd = new OleDbCommand(sql, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }

                    if (!HasTable("SyncLocks"))
                    {
                        Exec(@"CREATE TABLE SyncLocks (
                LockID TEXT(50) PRIMARY KEY,
                Reason TEXT(255),
                CreatedAt DATETIME,
                ExpiresAt DATETIME,
                MachineName TEXT(50),
                ProcessId LONG
            )");
                    }

                    if (!HasTable("Sessions"))
                    {
                        Exec(@"CREATE TABLE Sessions (
                        SessionID TEXT(255) PRIMARY KEY,
                        UserID TEXT(50) NOT NULL,
                        MachineName TEXT(100) NOT NULL,
                        StartTime DATETIME NOT NULL,
                        LastActivity DATETIME NOT NULL,
                        IsActive YESNO DEFAULT 1
                    )");
                    }

                    if (!HasTable("ChangeLog"))
                    {
                        Exec(@"CREATE TABLE ChangeLog (
                        ChangeID COUNTER PRIMARY KEY,
                        TableName TEXT(100) NOT NULL,
                        RecordID TEXT(255) NOT NULL,
                        Operation TEXT(255) NOT NULL,
                        [Timestamp] DATETIME NOT NULL,
                        Synchronized BIT NOT NULL DEFAULT 0
                    )");
                    }

                    if (!HasTable("SyncLog"))
                    {
                        Exec(@"CREATE TABLE SyncLog (
                        ID COUNTER PRIMARY KEY,
                        Operation TEXT(50),
                        Status TEXT(50),
                        Details TEXT(255),
                        [Timestamp] DATETIME
                    )");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erreur EnsureLockTablesExist: {ex.Message}");
                throw;
            }
        }


        /// <summary>
        /// Initialise la base de données locale pour un pays
        /// </summary>
        /// <param name="countryId">ID du pays</param>
        /// <returns>True si l'initialisation a réussi</returns>
        private async Task<bool> InitializeLocalDatabaseAsync(string countryId)
        {
            try
            {
                string countryDatabaseDirectory = GetParameter("CountryDatabaseDirectory");
                string countryDatabasePrefix = GetParameter("CountryDatabasePrefix") ?? "DB_";
                string dataDirectory = GetParameter("DataDirectory");

                string networkDbPath = Path.Combine(countryDatabaseDirectory, $"{countryDatabasePrefix}{countryId}.accdb");
                string localDbPath = Path.Combine(dataDirectory, $"{countryDatabasePrefix}{countryId}.accdb");

                // S'assurer que le répertoire local existe
                if (!Directory.Exists(dataDirectory))
                {
                    Directory.CreateDirectory(dataDirectory);
                }

                // Vérifier si la base locale existe
                if (!File.Exists(localDbPath))
                {
                    System.Diagnostics.Debug.WriteLine($"Base locale inexistante pour {countryId}, tentative de copie depuis le réseau");

                    // Vérifier que la base réseau existe
                    if (!File.Exists(networkDbPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"Base réseau inexistante pour {countryId}: {networkDbPath}");
                        return false;
                    }

                    // Copier la base réseau vers le local (FileShare.ReadWrite pour tolérer un .ldb actif)
                    await CopyFileAsync(networkDbPath, localDbPath, overwrite: true).ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine($"Base réseau copiée vers le local pour {countryId}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Base locale existante pour {countryId}, synchronisation nécessaire");
                    // La synchronisation sera gérée par le DatabaseService via InitializeLocalDatabaseAsync
                }

                // NOTE: ne pas dupliquer la base de lock en local. Les verrous doivent rester sur le réseau.
                // Ensure local ChangeLog schema exists if feature enabled
                if (_useLocalChangeLog)
                {
                    try { await EnsureLocalChangeLogSchemaAsync(countryId); } catch { }
                }

                // Marquer le service comme initialisé pour ce pays
                _isInitialized = true;

                // Invalider le cache DWINGS du pays précédent (si existant)
                try
                {
                    var prevDwPath = GetLocalDWDatabasePath(_currentCountryId);
                    DwingsService.InvalidateSharedCacheForPath(prevDwPath);
                }
                catch { }

                // Fermer la connexion réseau persistante de l'ancien pays
                CloseNetworkConnection();

                // Changer le pays courant
                _currentCountryId = countryId;

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erreur lors de l'initialisation de la base locale pour {countryId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Définit le pays courant et prépare l'environnement local.
        /// Règle: s'il existe des changements locaux non synchronisés, on les pousse d'abord vers le réseau,
        /// puis on recopie toutes les bases pertinentes (Reconciliation, Ambre, DW) du réseau vers le local.
        /// </summary>
        public Task<bool> SetCurrentCountryAsync(string countryId, bool suppressPush = false)
        {
            return SetCurrentCountryAsync(countryId, suppressPush, null);
        }

        /// <summary>
        /// Variante avec reporting de progression.
        /// </summary>
        public async Task<bool> SetCurrentCountryAsync(string countryId, bool suppressPush, Action<int, string> onProgress)
        {
            if (string.IsNullOrWhiteSpace(countryId))
                return false;

            onProgress?.Invoke(0, "Initialisation du pays...");

            // 0) Provisionner les bases RÉSEAU depuis des modèles si au moins une est absente (Reconciliation, AMBRE, Lock)
            try
            {
                bool needProvision = false;
                try
                {
                    var reconPath = GetNetworkReconciliationDbPath(countryId);
                    needProvision = !string.IsNullOrWhiteSpace(reconPath) && !File.Exists(reconPath);
                }
                catch { needProvision = true; }

                if (needProvision)
                {
                    onProgress?.Invoke(5, "Vérification des modèles réseau...");
                    await ProvisionNetworkFromTemplatesAsync(countryId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Template] Provision réseau ignorée: {ex.Message}");
            }

            // -----------------------------------------------------------------
            // 1) Initialiser/assurer la base locale principale (et positionner _currentCountryId)
            // -----------------------------------------------------------------
            onProgress?.Invoke(10, "Préparation de la base locale...");
            string dataDirectory_cold = GetParameter("DataDirectory");
            string countryDatabasePrefix_cold = GetParameter("CountryDatabasePrefix") ?? "DB_";
            string localDbPath_cold = Path.Combine(dataDirectory_cold,
                                                  $"{countryDatabasePrefix_cold}{countryId}.accdb");
            bool wasColdLocal = !File.Exists(localDbPath_cold);

            var initialized = await InitializeLocalDatabaseAsync(countryId);
            if (!initialized)
                return false;

            // ---------------------------------------------------------------
            // ★★ **NOUVELLE SECTION – Copie de la base DW** ★★
            // ---------------------------------------------------------------
            onProgress?.Invoke(15, "Mise à jour locale : DW…");
            bool dwCopied = await CopyNetworkToLocalDwAsync(countryId);
            if (!dwCopied)
            {
                // on prévient l’utilisateur (et on consigne l’erreur dans les logs)
                onProgress?.Invoke(17, "Erreur copie DW – voir les logs.");
                System.Diagnostics.Debug.WriteLine($"[SetCurrentCountryAsync] DW copy FAILED for {countryId}");
            }
            else
            {
                onProgress?.Invoke(18, "Base DW synchronisée.");
            }

            // 1.a) Build sync config (needed by entity processing) and ensure lock tables exist
            try
            {
                _syncConfig = BuildSyncConfiguration(countryId);
                EnsureLockTablesExist(_syncConfig.LockDatabasePath);
            }
            catch { }
            onProgress?.Invoke(30, "Configuration initialisée");

            // 1.b-bis) Schema drift: ALTER TABLE for any column declared in
            // RequiredColumns but missing from the live T_Reconciliation. Must run
            // BEFORE the push/pull below — both the change-tracking SELECT and the
            // INSERT in ReconciliationService.Crud reference every column (including
            // RemainingAmount and other Phase-2 fields), and OleDb fails the whole
            // statement when a column is unknown. Idempotent and best-effort: any
            // ALTER failure is logged in the report without aborting startup.
            //
            // We migrate the LOCAL database first (always reachable). If that adds
            // any column, we also try to migrate the NETWORK database so the very
            // next push can reference the new column on the remote side as well.
            // The network migration is best-effort: if Access has the file locked
            // by another user, OleDb will throw; we log and keep going — the next
            // user / next startup will retry.
            bool localSchemaChanged = false;
            try
            {
                var localCs = GetCurrentLocalConnectionString();
                if (!string.IsNullOrWhiteSpace(localCs))
                {
                    var report = await RecoTool.Infrastructure.Migrations.ReconciliationSchemaMigrator
                        .EnsureReconciliationColumnsAsync(localCs).ConfigureAwait(false);
                    if (!report.IsNoOp)
                        System.Diagnostics.Debug.WriteLine($"[SchemaMigrator/local] {report}");
                    localSchemaChanged = report.Added.Count > 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SchemaMigrator/local] ignored: {ex.Message}");
            }

            if (localSchemaChanged)
            {
                try
                {
                    var networkPath = GetNetworkReconciliationDbPath(countryId);
                    if (!string.IsNullOrWhiteSpace(networkPath) && File.Exists(networkPath))
                    {
                        var networkCs = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={networkPath};Persist Security Info=False;";
                        var report = await RecoTool.Infrastructure.Migrations.ReconciliationSchemaMigrator
                            .EnsureReconciliationColumnsAsync(networkCs).ConfigureAwait(false);
                        if (!report.IsNoOp)
                            System.Diagnostics.Debug.WriteLine($"[SchemaMigrator/network] {report}");
                    }
                }
                catch (Exception ex)
                {
                    // Common cause: another user has the file locked. Push will retry
                    // the migration implicitly via the next call to SetCurrentCountryAsync.
                    System.Diagnostics.Debug.WriteLine($"[SchemaMigrator/network] deferred: {ex.Message}");
                }
            }

            // 1.b-ter) Housekeeping: remove *.tmp_<guid> / *.compact_<guid>.accdb left
            // behind by atomic publish patterns when a previous run (this user or any
            // other) crashed between the temp-copy and the final move. 30-min age
            // threshold avoids racing with an in-flight publish from a concurrent
            // client. Cheap directory enumeration; safe on locked files.
            try
            {
                var localDataDir = Path.GetDirectoryName(GetLocalReconciliationDbPath(countryId));
                var remoteDataDir = Path.GetDirectoryName(GetNetworkReconciliationDbPath(countryId));
                var minAge = TimeSpan.FromMinutes(30);
                if (!string.IsNullOrWhiteSpace(localDataDir))
                    await PurgeOrphanedTempFilesAsync(localDataDir, minAge, _clock).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(remoteDataDir))
                    await PurgeOrphanedTempFilesAsync(remoteDataDir, minAge, _clock).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PurgeOrphanedTempFiles/startup] ignored: {ex.Message}");
            }

            // 1.b) Positionner également l'objet Country courant depuis les référentiels
            try
            {
                _currentCountry = await GetCountryByIdAsync(countryId);
            }
            catch
            {
                _currentCountry = null; // si échec de chargement, rester prudent
            }
            onProgress?.Invoke(35, "Référentiels pays chargés");

            // Augment cold-local if table exists but is empty (fresh DB): skip sync as well
            if (!wasColdLocal)
            {
                try
                {
                    if (await IsLocalReconciliationEmptyAsync(countryId))
                    {
                        wasColdLocal = true;
                    }
                }
                catch { }
            }

            // 2) Sync: push local pending changes then pull remote changes (direct, no SynchronizationService overhead)
            if (!suppressPush && !wasColdLocal)
            {
                try
                {
                    onProgress?.Invoke(40, "Push local changes...");
                    await PushReconciliationIfPendingAsync(countryId);
                    onProgress?.Invoke(45, "Pull remote changes...");
                    var pulled = await PullReconciliationFromNetworkAsync(countryId);
                    if (pulled > 0)
                    {
                        onProgress?.Invoke(48, $"Applied {pulled} row(s) from network");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[{nameof(SetCurrentCountryAsync)}] Sync error for {countryId}: {ex.Message}");
                }
            }
            else if (wasColdLocal)
            {
                onProgress?.Invoke(45, "Sync skipped (fresh local copy from network)");
            }

            // 3) Après le push+pull, ne plus recouvrir Reconciliation local depuis réseau.
            //    La synchronisation a déjà aligné local et réseau pour T_Reconciliation.
            //    Invalider le cache UI pour forcer un rechargement frais lors du prochain Refresh().
            try { ReconciliationService.InvalidateReconciliationViewCache(countryId); } catch { }
            onProgress?.Invoke(50, "Vérifications post-synchronisation pour RECON...");

            try
            {
                onProgress?.Invoke(60, "Mise à jour locale: AMBRE...");
                await CopyNetworkToLocalAmbreAsync(countryId);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AMBRE: échec copie réseau->local pour {countryId}: {ex.Message}"); }

            // -----------------------------------------------------------------
            // 3.b) **Pré‑chargement des caches DWINGS** – maintenant que le DW est présent
            // -----------------------------------------------------------------
            try
            {
                var dwSvc = new DwingsService(this);
                await dwSvc.PrimeCachesAsync().ConfigureAwait(false);
                onProgress?.Invoke(70, "Caches DWINGS pré‑chargés");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DwingsService] PrimeCachesAsync failed: {ex.Message}");
                // on ne bloque pas le flux ; on informe seulement l’utilisateur
                onProgress?.Invoke(70, "Erreur pré‑chargement DWINGS – voir les logs");
            }

            onProgress?.Invoke(80, "Initialisation pays terminée");
            return true;
        }

        /// <summary>
        /// S'il existe un répertoire de modèles (paramètre 'Template' ou 'TemplateDirectory'),
        /// copie les fichiers contenant 'XX' en remplaçant par le code pays vers le répertoire réseau
        /// (CountryDatabaseDirectory) sans écraser les fichiers existants.
        /// Exemple attendu: DB_XX.accdb, DB_XX_lock.accdb, AMBRE_XX.accdb, AMBRE_XX.zip, etc.
        /// </summary>
        private async Task ProvisionNetworkFromTemplatesAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return;
            string remoteDir = null;
            string templateDir = null;
            try
            {
                remoteDir = GetParameter("CountryDatabaseDirectory");
                templateDir = GetParameter("Template");
                if (string.IsNullOrWhiteSpace(templateDir)) templateDir = GetCentralConfig("Template");
                if (string.IsNullOrWhiteSpace(templateDir)) templateDir = GetParameter("TemplateDirectory");
            }
            catch { }

            if (string.IsNullOrWhiteSpace(remoteDir) || string.IsNullOrWhiteSpace(templateDir)) return;
            if (!Directory.Exists(templateDir)) return;

            try { Directory.CreateDirectory(remoteDir); } catch { }

            // Copier tous les fichiers contenant 'XX' (accdb/zip), en remplaçant par le code pays
            string[] patterns = new[] { "*XX*.accdb", "*XX*.zip" };
            foreach (var pattern in patterns)
            {
                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(templateDir, pattern, SearchOption.TopDirectoryOnly); } catch { }
                foreach (var src in files)
                {
                    try
                    {
                        var destName = Path.GetFileName(src).Replace("XX", countryId);
                        var destPath = Path.Combine(remoteDir, destName);
                        if (File.Exists(destPath)) continue; // ne pas écraser
                        // copie asynchrone best-effort
                        await CopyFileAsync(src, destPath, overwrite: false);
                    }
                    catch { }
                }
            }
        }

        #endregion

        #region Public Accessors for Referential Data
        /* moved to partial: Services/OfflineFirst/OfflineFirstService.Referentials.cs
           Contains: GetAmbreImportFields, GetAmbreTransforms, GetAmbreTransactionCodes,
                     UserFields, GetUserFilters, RefreshUserFiltersAsync */
        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Prépare une valeur .NET pour l'envoyer à OLE DB (gère les null/DBNull/DateTime/bool/etc.)
        /// </summary>
        private bool IsDiagSyncEnabled()
        {
            try
            {
                var flag = GetParameter("DiagSyncLog");
                if (string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) ?? string.Empty, "RecoTool");
                var marker = Path.Combine(dir, "diag_sync.on");
                return File.Exists(marker);
            }
            catch { }
            return false;
        }

        /* moved to partial: Services/OfflineFirst/OfflineFirstService.Crc.cs
           Contains: ComputeCrc32ForEntity, NormalizeForCrc, _crc32Table, Crc32Append, BuildCrc32Table */

        /// <summary>
        /// Récupère un pays par son identifiant depuis le cache. Charge les référentiels si nécessaire.
        /// </summary>
        public async Task<Country> GetCountryByIdAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return null;
            if (!_referentialsLoaded)
                await LoadReferentialsAsync();
            lock (_referentialLock)
            {
                return _countries.FirstOrDefault(c => string.Equals(c?.CNT_Id, countryId, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Récupère toutes les lignes d'une table en entités, depuis la base locale du pays spécifié.
        /// </summary>
        public async Task<List<Entity>> GetEntitiesAsync(string countryId, string tableName)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));
            if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("tableName est requis", nameof(tableName));

            // Si nécessaire, basculer sur le bon pays
            if (!string.Equals(countryId, _currentCountryId, StringComparison.OrdinalIgnoreCase))
            {
                var ok = await SetCurrentCountryAsync(countryId, suppressPush: true);
                if (!ok) throw new InvalidOperationException($"Impossible d'initialiser la base locale pour {countryId}");
            }
            EnsureInitialized();

            var list = new List<Entity>();
            // Choisir la base locale cible en fonction de la table
            string targetDbPath;
            if (string.Equals(tableName, "T_Data_Ambre", StringComparison.OrdinalIgnoreCase))
            {
                targetDbPath = GetLocalAmbreDbPath(_currentCountryId);
            }
            else
            {
                targetDbPath = GetLocalReconciliationDbPath(_currentCountryId);
            }

            if (string.IsNullOrWhiteSpace(targetDbPath) || !File.Exists(targetDbPath))
                throw new FileNotFoundException($"Base locale introuvable pour la table '{tableName}'", targetDbPath ?? "<null>");

            var connStr = AceConn(targetDbPath);
            using (var connection = new OleDbConnection(connStr))
            {
                await connection.OpenAsync();
                using (var cmd = new OleDbCommand($"SELECT * FROM [{tableName}]", connection))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var ent = new Entity { TableName = tableName };
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            var col = reader.GetName(i);
                            var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            ent.Properties[col] = val;
                        }
                        list.Add(ent);
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Lance une synchronisation avec retour de résultat et progression.
        /// </summary>
        public async Task<SyncResult> SynchronizeAsync(string countryId, CancellationToken? cancellationToken = null, Action<int, string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));

            // Coalesce: if a sync for this country is already running, return the same Task
            if (_activeSyncs.TryGetValue(countryId, out var existing))
            {
                return await existing;
            }

            async Task<SyncResult> RunSyncAsync()
            {
                // Per-country sync semaphore to serialize syncs (safety net)
                var sem = _syncSemaphores.GetOrAdd(countryId, _ => new SemaphoreSlim(1, 1));
                if (!await sem.WaitAsync(0))
                {
                    return new SyncResult { Success = false, Message = "Synchronization already in progress for this country" };
                }
                try
                {
                    // S'assurer que la configuration est prête pour le pays demandé
                    if (!string.Equals(countryId, _currentCountryId, StringComparison.OrdinalIgnoreCase))
                    {
                        var ok = await SetCurrentCountryAsync(countryId, suppressPush: true);
                        if (!ok)
                            return new SyncResult { Success = false, Message = $"Initialisation impossible pour {countryId}" };
                    }

                    // Pause automatique si verrou d'import actif
                    try
                    {
                        var lockActive = await IsGlobalLockActiveAsync();
                        if (lockActive)
                        {
                            onProgress?.Invoke(0, "Synchronisation en pause: verrou d'import actif");
                            return new SyncResult { Success = false, Message = "Import lock active - sync paused" };
                        }
                    }
                    catch { /* ignorer les erreurs lors de la vérification du verrou */ }

                    // Detect remote changes since last sync to avoid false no-op (prefer Version watermark; fallback to LastModified)
                    bool remoteHasChanges = false;
                    try
                    {
                        var remotePath = GetNetworkReconciliationDbPath(_currentCountryId);
                        if (!string.IsNullOrWhiteSpace(remotePath) && File.Exists(remotePath))
                        {
                            // Read watermarks from local control DB (_SyncConfig)
                            DateTime lastSync = DateTime.MinValue;
                            long lastSyncVersion = -1;
                            try
                            {
                                using (var ctrl = new OleDbConnection(GetControlConnectionString()))
                                {
                                    await ctrl.OpenAsync();
                                    // Ensure _SyncConfig exists
                                    var schema = ctrl.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, null, "TABLE" });
                                    bool has = schema != null && schema.Rows.OfType<System.Data.DataRow>()
                                        .Any(r => string.Equals(Convert.ToString(r["TABLE_NAME"]), "_SyncConfig", StringComparison.OrdinalIgnoreCase));
                                    if (!has)
                                    {
                                        using (var create = new OleDbCommand("CREATE TABLE _SyncConfig (ConfigKey TEXT(255) PRIMARY KEY, ConfigValue MEMO)", ctrl))
                                            await create.ExecuteNonQueryAsync();
                                    }
                                    // Try version first
                                    try
                                    {
                                        using (var verCmd = new OleDbCommand("SELECT ConfigValue FROM _SyncConfig WHERE ConfigKey = 'LastSyncVersion'", ctrl))
                                        using (var r = await verCmd.ExecuteReaderAsync())
                                        {
                                            if (await r.ReadAsync())
                                            {
                                                var val = r.IsDBNull(0) ? null : r.GetString(0);
                                                if (!string.IsNullOrWhiteSpace(val)) long.TryParse(val, out lastSyncVersion);
                                            }
                                        }
                                    }
                                    catch { }
                                    using (var cmd = new OleDbCommand("SELECT ConfigValue FROM _SyncConfig WHERE ConfigKey = 'LastSyncTimestamp'", ctrl))
                                    using (var reader = await cmd.ExecuteReaderAsync())
                                    {
                                        if (await reader.ReadAsync())
                                        {
                                            var val = reader.IsDBNull(0) ? null : reader.GetString(0);
                                            if (!string.IsNullOrWhiteSpace(val))
                                            {
                                                DateTime parsed;
                                                if (DateTime.TryParse(val, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out parsed))
                                                    lastSync = parsed.ToUniversalTime();
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }

                            // Quick count on remote using Version watermark if available; else LastModified timestamp
                            try
                            {
                                string conn = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={remotePath};Persist Security Info=False;";
                                using (var remote = new OleDbConnection(conn))
                                {
                                    await remote.OpenAsync();
                                    if (lastSyncVersion >= 0)
                                    {
                                        using (var countCmd = new OleDbCommand("SELECT COUNT(*) FROM T_Reconciliation WHERE Version > ?", remote))
                                        {
                                            // Access/ACE is sensitive to parameter types. Use explicit Integer for Version
                                            var p = new OleDbParameter("@p1", OleDbType.Integer) { Value = lastSyncVersion };
                                            countCmd.Parameters.Add(p);
                                            var obj = await countCmd.ExecuteScalarAsync();
                                            int cnt;
                                            if (obj != null && int.TryParse(Convert.ToString(obj), out cnt))
                                                remoteHasChanges = cnt > 0;
                                        }
                                    }
                                    else
                                    {
                                        using (var countCmd = new OleDbCommand("SELECT COUNT(*) FROM T_Reconciliation WHERE LastModified > ?", remote))
                                        {
                                            // Access/ACE: use explicit Date type for DateTime parameters
                                            // Use Access lower bound (1899-12-30) when no lastSync available
                                            var lowerBound = new DateTime(1899, 12, 30);
                                            var dt = lastSync == DateTime.MinValue ? lowerBound : lastSync;
                                            var p = new OleDbParameter("@p1", OleDbType.Date) { Value = dt };
                                            countCmd.Parameters.Add(p);
                                            var obj = await countCmd.ExecuteScalarAsync();
                                            int cnt;
                                            if (obj != null && int.TryParse(Convert.ToString(obj), out cnt))
                                                remoteHasChanges = cnt > 0;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    // Push local pending changes first (best-effort)
                    if (_useLocalChangeLog)
                    {
                        try
                        {
                            onProgress?.Invoke(1, "Poussée des changements locaux en attente...");
                            await PushReconciliationIfPendingAsync(_currentCountryId);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[SYNC] Pre-pull push failed: {ex.Message}");
                            // continue with reconcile; entries remain pending locally
                        }
                    }

                    // Fast-path: comparer uniquement la base de réconciliation (AMBRE est géré par import)
                    try
                    {
                        var localPath = GetLocalReconciliationDbPath(_currentCountryId);
                        var remotePath = GetNetworkReconciliationDbPath(_currentCountryId);
                        if (!string.IsNullOrWhiteSpace(localPath) && !string.IsNullOrWhiteSpace(remotePath)
                            && File.Exists(localPath) && File.Exists(remotePath))
                        {
                            var lfi = new FileInfo(localPath);
                            var rfi = new FileInfo(remotePath);
                            bool filesLikelyIdentical = FilesAreEqual(lfi, rfi);

                            if (filesLikelyIdentical)
                            {
                                // Vérifier les changements en attente dans la base de lock
                                var tracker = new OfflineFirstAccess.ChangeTracking.ChangeTracker(GetChangeLogConnectionString(_currentCountryId));
                                var unsynced = await tracker.GetUnsyncedChangesAsync();
                                // If files are identical and there are no local pending changes, we can safely skip sync regardless of remoteHasChanges
                                // This avoids redundant row-level reapplication after a fresh local copy or after restart
                                if (unsynced == null || !unsynced.Any())
                                {
                                    onProgress?.Invoke(100, "Bases identiques (RECON) - aucune synchronisation nécessaire.");
                                    return new SyncResult { Success = true, Message = "No-op (identique)" };
                                }
                            }
                        }
                    }
                    catch { /* best-effort, en cas d'erreur on continue avec la synchro normale */ }

                    // Déterminer les tables de réconciliation à synchroniser
                    var reconTables = new List<string>();
                    var syncTables = GetParameter("SyncTables");
                    if (!string.IsNullOrWhiteSpace(syncTables))
                    {
                        foreach (var t in syncTables.Split(','))
                        {
                            var name = t?.Trim();
                            if (!string.IsNullOrEmpty(name) && name.StartsWith("T_Reconciliation", StringComparison.OrdinalIgnoreCase))
                                reconTables.Add(name);
                        }
                    }
                    if (reconTables.Count == 0)
                    {
                        reconTables.Add("T_Reconciliation");
                    }

                    onProgress?.Invoke(0, "Push local changes...");
                    await PushReconciliationIfPendingAsync(_currentCountryId);
                    onProgress?.Invoke(50, "Pull remote changes...");
                    var pulled = await PullReconciliationFromNetworkAsync(_currentCountryId);
                    var reconRes = new SyncResult { Success = true, Message = $"Applied {pulled} row(s)" };

                    if (reconRes != null && reconRes.Success)
                    {
                        var key = _currentCountryId;
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            _lastSyncTimes[key] = _clock.UtcNow;
                        }
                        // Persist LastSyncTimestamp in _SyncConfig (ISO UTC)
                        try
                        {
                            if (string.IsNullOrWhiteSpace(key)) return reconRes; // nothing to persist without a country key
                            var iso = _lastSyncTimes[key].ToString("o");
                            using (var connection = new OleDbConnection(GetControlConnectionString()))
                            {
                                await connection.OpenAsync();
                                // Ensure _SyncConfig exists locally
                                var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, null, "TABLE" });
                                bool tableExists = schema != null && schema.Rows.OfType<System.Data.DataRow>()
                                    .Any(r => string.Equals(r["TABLE_NAME"].ToString(), "_SyncConfig", StringComparison.OrdinalIgnoreCase));
                                if (!tableExists)
                                {
                                    using (var create = new OleDbCommand("CREATE TABLE _SyncConfig (ConfigKey TEXT(255) PRIMARY KEY, ConfigValue MEMO)", connection))
                                    {
                                        await create.ExecuteNonQueryAsync();
                                    }
                                }

                                using (var update = new OleDbCommand("UPDATE _SyncConfig SET ConfigValue = @val WHERE ConfigKey = 'LastSyncTimestamp'", connection))
                                {
                                    update.Parameters.Add(new OleDbParameter("@val", OleDbType.LongVarWChar) { Value = (object)iso ?? DBNull.Value });
                                    int rows = await update.ExecuteNonQueryAsync();
                                    if (rows == 0)
                                    {
                                        using (var insert = new OleDbCommand("INSERT INTO _SyncConfig (ConfigKey, ConfigValue) VALUES ('LastSyncTimestamp', @val)", connection))
                                        {
                                            insert.Parameters.Add(new OleDbParameter("@val", OleDbType.LongVarWChar) { Value = (object)iso ?? DBNull.Value });
                                            await insert.ExecuteNonQueryAsync();
                                        }
                                    }
                                }

                                // Also persist LastSyncVersion if the remote table supports it
                                try
                                {
                                    var remotePath = GetNetworkReconciliationDbPath(key);
                                    if (!string.IsNullOrWhiteSpace(remotePath) && File.Exists(remotePath))
                                    {
                                        var connStr = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={remotePath};Persist Security Info=False;";
                                        long maxVer = -1;
                                        using (var remote = new OleDbConnection(connStr))
                                        {
                                            await remote.OpenAsync();
                                            maxVer = await OleDbUtils.GetMaxVersionAsync(remote, "T_Reconciliation");
                                        }

                                        if (maxVer >= 0)
                                        {
                                            using (var up = new OleDbCommand("UPDATE _SyncConfig SET ConfigValue = @v WHERE ConfigKey = 'LastSyncVersion'", connection))
                                            {
                                                up.Parameters.Add(new OleDbParameter("@v", OleDbType.LongVarWChar) { Value = maxVer.ToString() });
                                                int rows = await up.ExecuteNonQueryAsync();
                                                if (rows == 0)
                                                {
                                                    using (var ins = new OleDbCommand("INSERT INTO _SyncConfig (ConfigKey, ConfigValue) VALUES ('LastSyncVersion', @v)", connection))
                                                    {
                                                        ins.Parameters.Add(new OleDbParameter("@v", OleDbType.LongVarWChar) { Value = maxVer.ToString() });
                                                        await ins.ExecuteNonQueryAsync();
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                        return reconRes;
                    }
                    return reconRes ?? new SyncResult { Success = false, Message = "Résultat RECON nul" };
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SYNC] Erreur: {ex.Message}");
                    return new SyncResult { Success = false, Message = ex.Message };
                }
                finally
                {
                    try { sem.Release(); } catch { }
                }
            }

            var runner = RunSyncAsync();
            if (!_activeSyncs.TryAdd(countryId, runner))
            {
                // Race: another runner just got added
                if (_activeSyncs.TryGetValue(countryId, out var current)) return await current;
            }
            try
            {
                return await runner;
            }
            finally
            {
                _activeSyncs.TryRemove(countryId, out _);
            }
        }

        /// <summary>
        /// Lance une synchronisation simple sans progression. Retourne true si succès.
        /// </summary>
        public async Task<bool> SynchronizeData()
        {
            if (string.IsNullOrEmpty(_currentCountryId)) return false;
            var res = await SynchronizeAsync(_currentCountryId, null, null);
            return res != null && res.Success;
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Libère les ressources utilisées par le service
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Libère les ressources utilisées par le service
        /// </summary>
        /// <param name="disposing">True si appelé depuis Dispose()</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Arrêter la surveillance des changements
                    try
                    {
                        //StopWatching();
                    }
                    catch
                    {
                        // Ignorer les erreurs lors de la libération
                    }

                    // Rien à libérer concernant l'ancien service de base de données (supprimé)

                    // Fermer la connexion réseau persistante
                    try { CloseNetworkConnection(); } catch { }
                }

                _disposed = true;
            }
        }

        #endregion
    }

}
#endregion