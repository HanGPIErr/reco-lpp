using System;
using System.Data;
using System.Data.OleDb;
using System.Threading;
using System.Threading.Tasks;

namespace RecoTool.Infrastructure.DataAccess
{
    /// <summary>
    /// Manages a single shared persistent OleDb connection to the referential database (DB_Referentiels).
    /// All read/write services should use <see cref="GetConnectionAsync"/> instead of opening their own connections.
    /// The connection is lazily created on first access and automatically recreated if broken.
    /// Thread-safe: uses a SemaphoreSlim to serialize connection creation.
    /// </summary>
    public sealed class ReferentialConnectionPool : IDisposable
    {
        private static ReferentialConnectionPool _instance;
        private static readonly object _instanceLock = new object();

        private readonly string _connectionString;
        private OleDbConnection _connection;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        private ReferentialConnectionPool(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <summary>
        /// Initializes the singleton with the given connection string.
        /// Safe to call multiple times — subsequent calls with the same connection string are ignored.
        /// If the connection string changes, the old pool is disposed and replaced.
        /// </summary>
        public static void Initialize(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentNullException(nameof(connectionString));

            lock (_instanceLock)
            {
                if (_instance != null && string.Equals(_instance._connectionString, connectionString, StringComparison.OrdinalIgnoreCase))
                    return; // Already initialized with same connection string

                // Replace with new pool
                _instance?.Dispose();
                _instance = new ReferentialConnectionPool(connectionString);
            }
        }

        /// <summary>
        /// Returns the shared singleton instance.
        /// Returns null if not yet initialized (caller should fall back to ad-hoc connection).
        /// </summary>
        public static ReferentialConnectionPool Instance
        {
            get
            {
                lock (_instanceLock)
                {
                    return _instance;
                }
            }
        }

        /// <summary>
        /// Returns a healthy open connection to the referential database.
        /// If the current connection is broken or closed, it is automatically recreated.
        /// The caller MUST NOT dispose or close the returned connection.
        /// </summary>
        public async Task<OleDbConnection> GetConnectionAsync(CancellationToken token = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReferentialConnectionPool));

            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_connection != null && _connection.State == ConnectionState.Open)
                    return _connection;

                // Close broken connection
                CloseConnectionInternal();

                // Create new connection
                var conn = new OleDbConnection(_connectionString);
                await conn.OpenAsync(token).ConfigureAwait(false);
                _connection = conn;
                System.Diagnostics.Debug.WriteLine("[ReferentialConnectionPool] New connection opened.");
                return _connection;
            }
            catch
            {
                CloseConnectionInternal();
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Returns a healthy open connection synchronously.
        /// Prefer <see cref="GetConnectionAsync"/> when possible.
        /// </summary>
        public OleDbConnection GetConnection()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ReferentialConnectionPool));

            _gate.Wait();
            try
            {
                if (_connection != null && _connection.State == ConnectionState.Open)
                    return _connection;

                CloseConnectionInternal();

                var conn = new OleDbConnection(_connectionString);
                conn.Open();
                _connection = conn;
                System.Diagnostics.Debug.WriteLine("[ReferentialConnectionPool] New connection opened (sync).");
                return _connection;
            }
            catch
            {
                CloseConnectionInternal();
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Closes the current connection (e.g., before compaction or during import).
        /// The connection will be automatically recreated on next access.
        /// </summary>
        public void CloseConnection()
        {
            _gate.Wait();
            try
            {
                CloseConnectionInternal();
                System.Diagnostics.Debug.WriteLine("[ReferentialConnectionPool] Connection closed by caller.");
            }
            finally
            {
                _gate.Release();
            }
        }

        private void CloseConnectionInternal()
        {
            try { _connection?.Close(); } catch { }
            try { _connection?.Dispose(); } catch { }
            _connection = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseConnectionInternal();
            _gate.Dispose();
        }
    }
}
