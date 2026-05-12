using System;
using System.Data.OleDb;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RecoTool.Infrastructure.DataAccess
{
    /// <summary>
    /// Wraps synchronous <see cref="OleDbConnection"/> work in <see cref="Task.Run(Action)"/> so callers on the UI
    /// thread don't block on Access I/O.
    ///
    /// <para>
    /// <b>Why this exists.</b> On .NET Framework 4.8, the OleDb provider does <i>not</i> implement true async I/O.
    /// <see cref="OleDbConnection.OpenAsync()"/>, <see cref="OleDbCommand.ExecuteNonQueryAsync()"/>,
    /// <see cref="OleDbCommand.ExecuteScalarAsync()"/>, <see cref="OleDbDataReader.ReadAsync()"/> etc. all run
    /// their underlying synchronous code on the calling thread before returning an already-completed Task. As a
    /// result, <c>await someOleDbCall()</c> does not yield to a worker thread — the UI freezes for the duration
    /// of the database call.
    /// </para>
    /// <para>
    /// This helper makes the off-thread dispatch explicit. Pair it with synchronous OleDb calls (e.g.
    /// <see cref="OleDbConnection.Open()"/>, <see cref="OleDbCommand.ExecuteNonQuery()"/>) inside the
    /// delegate — the whole block runs on a thread-pool worker.
    /// </para>
    /// <para>
    /// <b>Cancellation contract.</b> The <see cref="CancellationToken"/> is checked once before <see cref="Task.Run(Action)"/>
    /// schedules the delegate. Once the synchronous OleDb call is in flight it cannot be interrupted — OleDb has
    /// no cooperative cancellation. Use the token to skip work that has already become irrelevant by the time the
    /// worker thread picks it up.
    /// </para>
    /// </summary>
    public static class OleDbAsyncExecutor
    {
        /// <summary>Logger for diagnostics. Defaults to <see cref="NullLogger.Instance"/>; replace via DI bootstrap.</summary>
        public static ILogger Logger { get; set; } = NullLogger.Instance;

        /// <summary>
        /// Runs a synchronous OleDb action on the thread pool. The connection is supplied by the caller and is
        /// neither opened nor closed by this helper.
        /// </summary>
        /// <param name="work">Synchronous action that operates on the supplied connection.</param>
        /// <param name="conn">The connection passed to <paramref name="work"/>. May be <c>null</c> if the delegate doesn't use it.</param>
        /// <param name="ct">Token checked before the delegate is invoked. Cannot interrupt an in-flight OleDb call.</param>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is <c>null</c>.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled before the delegate started.</exception>
        public static Task RunAsync(Action<OleDbConnection> work, OleDbConnection conn, CancellationToken ct = default)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    work(conn);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "OleDbAsyncExecutor.RunAsync delegate threw {ExceptionType}", ex.GetType().Name);
                    throw;
                }
            }, ct);
        }

        /// <summary>
        /// Runs a synchronous OleDb function on the thread pool and returns its result. The connection is supplied
        /// by the caller and is neither opened nor closed by this helper.
        /// </summary>
        /// <typeparam name="T">Result type returned by the delegate.</typeparam>
        /// <param name="work">Synchronous function that operates on the supplied connection.</param>
        /// <param name="conn">The connection passed to <paramref name="work"/>. May be <c>null</c> if the delegate doesn't use it.</param>
        /// <param name="ct">Token checked before the delegate is invoked. Cannot interrupt an in-flight OleDb call.</param>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is <c>null</c>.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled before the delegate started.</exception>
        public static Task<T> RunAsync<T>(Func<OleDbConnection, T> work, OleDbConnection conn, CancellationToken ct = default)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return work(conn);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "OleDbAsyncExecutor.RunAsync<T> delegate threw {ExceptionType}", ex.GetType().Name);
                    throw;
                }
            }, ct);
        }

        /// <summary>
        /// Opens an <see cref="OleDbConnection"/> from <paramref name="connectionString"/> on the thread pool,
        /// runs <paramref name="work"/>, then disposes the connection — all off the caller's thread so the
        /// synchronous <see cref="OleDbConnection.Open()"/> call doesn't block.
        /// </summary>
        /// <typeparam name="T">Result type returned by the delegate.</typeparam>
        /// <param name="connectionString">OleDb connection string used to construct the connection.</param>
        /// <param name="work">Synchronous function that runs against the opened connection.</param>
        /// <param name="ct">Token checked before <see cref="OleDbConnection.Open()"/> is invoked.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connectionString"/> or <paramref name="work"/> is <c>null</c>.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled before the work started.</exception>
        public static Task<T> RunWithConnectionAsync<T>(string connectionString, Func<OleDbConnection, T> work, CancellationToken ct = default)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            if (work == null) throw new ArgumentNullException(nameof(work));

            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var connection = new OleDbConnection(connectionString);
                try
                {
                    connection.Open();
                    return work(connection);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "OleDbAsyncExecutor.RunWithConnectionAsync delegate threw {ExceptionType}", ex.GetType().Name);
                    throw;
                }
                finally
                {
                    try { connection.Dispose(); } catch { /* best-effort cleanup */ }
                }
            }, ct);
        }

        /// <summary>
        /// Opens an <see cref="OleDbConnection"/>, begins a transaction, runs <paramref name="work"/>, then
        /// commits (or rolls back on exception) — all on the thread pool. The connection and transaction are
        /// always disposed.
        /// </summary>
        /// <typeparam name="T">Result type returned by the delegate.</typeparam>
        /// <param name="connectionString">OleDb connection string used to construct the connection.</param>
        /// <param name="work">Synchronous function that runs inside the transaction.</param>
        /// <param name="ct">Token checked before <see cref="OleDbConnection.Open()"/> is invoked.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connectionString"/> or <paramref name="work"/> is <c>null</c>.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled before the work started.</exception>
        public static Task<T> RunInTransactionAsync<T>(string connectionString, Func<OleDbConnection, OleDbTransaction, T> work, CancellationToken ct = default)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            if (work == null) throw new ArgumentNullException(nameof(work));

            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var connection = new OleDbConnection(connectionString);
                OleDbTransaction tx = null;
                try
                {
                    connection.Open();
                    tx = connection.BeginTransaction();

                    T result;
                    try
                    {
                        result = work(connection, tx);
                    }
                    catch
                    {
                        try { tx.Rollback(); } catch (Exception rbEx) { Logger.LogWarning(rbEx, "OleDbAsyncExecutor.RunInTransactionAsync rollback failed"); }
                        throw;
                    }

                    tx.Commit();
                    return result;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "OleDbAsyncExecutor.RunInTransactionAsync delegate threw {ExceptionType}", ex.GetType().Name);
                    throw;
                }
                finally
                {
                    try { tx?.Dispose(); } catch { /* best-effort cleanup */ }
                    try { connection.Dispose(); } catch { /* best-effort cleanup */ }
                }
            }, ct);
        }
    }
}
