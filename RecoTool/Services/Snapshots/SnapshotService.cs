using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using OfflineFirstAccess.Helpers;
using RecoTool.Services.DTOs;

namespace RecoTool.Services.Snapshots
{
    /// <summary>
    /// Captures point-in-time copies of the DWINGS + Reconciliation databases at the start of an
    /// import so they can later be diffed against the current state via
    /// <see cref="SnapshotComparisonService"/>. AMBRE is intentionally NOT snapshotted — its rows
    /// are effectively immutable between imports, so a field-level diff would be pure noise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Local layout.</b> A single "run" is materialised by a <c>manifest_{timestamp}.json</c>
    /// file + up to two <c>{type}_{countryId}_{timestamp}.accdb</c> siblings (DW, Reco).
    /// Everything lives under <c>{DataDirectory}\Snapshots\{countryId}\</c> (override via T_Param
    /// <c>SnapshotsDirectory</c>). Retention is bounded by <c>SnapshotsRetainCount</c> (default 5)
    /// so local disk usage stays at ~10 files per country.
    /// </para>
    /// <para>
    /// <b>Shared layout.</b> On <see cref="CompleteAsync"/> the manifest + both .accdb files are
    /// zipped and atomically published to <c>{CountryDatabaseDirectory}\Snapshots\{countryId}\</c>
    /// as <c>snapshot_{countryId}_{stamp}.zip</c>. Any other client will transparently pull new
    /// zips via <see cref="EnsureLatestAsync"/> before each diff, so a colleague who imported
    /// yesterday remains visible to whoever opens the view today. Network retention is separate
    /// (<c>SnapshotsNetworkRetainCount</c>, default 10) because the shared folder is the only
    /// place cross-user history survives local rotation.
    /// </para>
    /// <para>
    /// This service is <b>append-only</b> for writes and <b>read-only</b> for reads. It never
    /// mutates the live databases. Failures never propagate to the caller — the pipeline is
    /// audit, not correctness.
    /// </para>
    /// </remarks>
    public sealed class SnapshotService
    {
        internal const string ManifestPrefix = "manifest_";
        internal const string TimestampFormat = "yyyyMMdd_HHmmss";
        private const int DefaultRetainCount = 5;
        // Network retention defaults to 2× local so the "Full history" popup can browse further
        // back than a single user's local cache without blowing up network disk.
        private const int DefaultNetworkRetainCount = 10;
        // Bucket how often we re-scan the network share per country. Under this throttle, repeated
        // view refreshes within a short window don't spam the file share with directory listings.
        private static readonly TimeSpan PullRefreshInterval = TimeSpan.FromSeconds(15);

        // We parse the Data Source token ourselves instead of instantiating an OleDbConnection just
        // to read the path — saves a round-trip and avoids holding an open handle on the file.
        private static readonly Regex DataSourceRegex =
            new Regex(@"Data Source\s*=\s*(?<path>[^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches the trailing "_yyyyMMdd_HHmmss" suffix of a published zip filename. Capture 1 =
        // the raw timestamp (same format we use locally). Anchored with \z so a stray timestamp
        // earlier in the name cannot shadow the true stamp.
        private static readonly Regex SnapshotZipStampRegex =
            new Regex(@"_(?<stamp>\d{8}_\d{6})\z", RegexOptions.Compiled);

        // Per-country gate so only one pull/publish is in flight at a time.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _networkGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        // Rate-limit the network scan — see PullRefreshInterval.
        private static readonly ConcurrentDictionary<string, DateTime> _lastPullUtc =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly OfflineFirstService _ofs;

        public SnapshotService(OfflineFirstService ofs)
        {
            _ofs = ofs ?? throw new ArgumentNullException(nameof(ofs));
        }

        // ──────────────────────────────────────────────────────────────────────────────────────
        // Write path
        // ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Copies the live AMBRE / DW / Reconciliation files for <paramref name="countryId"/> to
        /// the snapshots folder and writes an initial manifest. Call once, early in the import
        /// pipeline <b>before</b> any mutation takes place.
        /// </summary>
        /// <returns>
        /// A handle to pass to <see cref="CompleteAsync"/> when the run ends. Returns <c>null</c>
        /// if the snapshot location cannot be resolved — callers should treat this as "journaling
        /// disabled for this run" and continue.
        /// </returns>
        public async Task<SnapshotHandle> BeginAsync(string countryId, RunKind kind, string triggeredBy)
        {
            if (string.IsNullOrWhiteSpace(countryId))
                throw new ArgumentException("countryId is required", nameof(countryId));

            string countryDir;
            try
            {
                countryDir = EnsureCountryDir(countryId);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: cannot resolve snapshot directory for '{countryId}': {ex.Message}");
                return null;
            }

            // Pre-rotation: remove stale runs BEFORE we create the new one. Keep (N-1) so the
            // disk peak is exactly N runs × 2 files + 1 manifest per run right after CompleteAsync.
            try { PurgeOld(countryId, Math.Max(1, GetRetainCount() - 1)); }
            catch (Exception ex) { LogManager.Warning($"Snapshot: pre-rotation failed: {ex.Message}"); }

            var started = DateTime.UtcNow;
            var stamp = started.ToString(TimestampFormat);
            var runId = Guid.NewGuid();

            // AMBRE is intentionally NOT snapshotted: rows there are effectively immutable between
            // imports (once reconciled → archived, everything else is brand-new data). A field-level
            // diff would be pure noise. We only snapshot DWINGS (status churn: MT_STATUS, invoice
            // status, payment method…) and Reconciliation (user edits + rule applications).
            string dwLive = _ofs.GetLocalDWDatabasePath(countryId);
            string recoLive = GetLocalReconciliationPath(countryId);

            // Snapshot paths. We copy even when some live files are missing — the manifest will
            // have null for those and the comparison service will treat them as "nothing to diff".
            string dwSnap = !string.IsNullOrEmpty(dwLive) && File.Exists(dwLive)
                ? Path.Combine(countryDir, $"DW_{countryId}_{stamp}.accdb")
                : null;
            string recoSnap = !string.IsNullOrEmpty(recoLive) && File.Exists(recoLive)
                ? Path.Combine(countryDir, $"Reco_{countryId}_{stamp}.accdb")
                : null;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            // Run both copies in parallel — they hit distinct files and the OS schedules them
            // fine. Each is also wrapped in its own try/catch so one failure doesn't block the other.
            await Task.WhenAll(
                SafeCopyAsync(dwLive,    dwSnap),
                SafeCopyAsync(recoLive,  recoSnap)).ConfigureAwait(false);
            timer.Stop();

            var manifest = new Manifest
            {
                RunId = runId,
                CountryId = countryId,
                Kind = kind.ToString(),
                StartedUtc = started,
                TriggeredBy = triggeredBy ?? string.Empty,
                AmbreSnapshot = null,                                   // legacy field, never populated on new runs
                DwingsSnapshot = dwSnap == null ? null : Path.GetFileName(dwSnap),
                RecoSnapshot = recoSnap == null ? null : Path.GetFileName(recoSnap),
            };
            var manifestPath = Path.Combine(countryDir, $"{ManifestPrefix}{stamp}.json");
            try { WriteManifest(manifestPath, manifest); }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: failed to write manifest: {ex.Message}");
                return null;
            }

            LogManager.Info($"[PERF] Snapshot created for {countryId} in {timer.ElapsedMilliseconds}ms " +
                            $"(dw={dwSnap != null}, reco={recoSnap != null})");

            return new SnapshotHandle
            {
                RunId = runId,
                CountryId = countryId,
                Kind = kind,
                StartedUtc = started,
                AmbreSnapshotPath = null,                               // legacy DTO field, no longer populated
                DwingsSnapshotPath = dwSnap,
                RecoSnapshotPath = recoSnap,
                ManifestPath = manifestPath,
            };
        }

        /// <summary>
        /// Updates the manifest with final counters + success flag. No-op when
        /// <paramref name="handle"/> is null or the manifest file is missing.
        /// </summary>
        public async Task CompleteAsync(
            SnapshotHandle handle,
            int newCount, int updatedCount, int archivedCount,
            bool success, string errorMessage = null)
        {
            if (handle == null || string.IsNullOrEmpty(handle.ManifestPath) || !File.Exists(handle.ManifestPath))
                return;

            try
            {
                var manifest = await Task.Run(() => ReadManifest(handle.ManifestPath)).ConfigureAwait(false);
                if (manifest == null) return;
                manifest.EndedUtc = DateTime.UtcNow;
                manifest.NewCount = newCount;
                manifest.UpdatedCount = updatedCount;
                manifest.ArchivedCount = archivedCount;
                manifest.Success = success;
                manifest.ErrorMessage = errorMessage ?? string.Empty;
                await Task.Run(() => WriteManifest(handle.ManifestPath, manifest)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: CompleteAsync failed: {ex.Message}");
            }

            // Publish to the shared network folder so other users see the diff on their next
            // view load. Best-effort and separately guarded: a publish failure must not mask
            // the import outcome the caller is waiting for.
            try { await PublishAsync(handle).ConfigureAwait(false); }
            catch (Exception ex) { LogManager.Warning($"Snapshot: publish failed: {ex.Message}"); }
        }

        // ──────────────────────────────────────────────────────────────────────────────────────
        // Network sync — publish (local → shared) and pull (shared → local cache)
        // ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Zips the manifest + DWINGS + Reconciliation snapshot files into a single
        /// <c>snapshot_{country}_{stamp}.zip</c> and atomically publishes it to the shared
        /// snapshots folder (<c>{CountryDatabaseDirectory}\Snapshots\{country}\</c>). Idempotent:
        /// re-publishing the same run overwrites the existing zip.
        /// </summary>
        /// <remarks>
        /// The shared folder is the source of truth across users. Every other client opportunistically
        /// pulls new zips into its local cache via <see cref="EnsureLatestAsync"/> before each diff,
        /// so a colleague reopening the view the next day sees the same activity indicators as the
        /// user who did the import.
        /// <para>
        /// Fails silently (log only). Callers must not rely on publish success — the local snapshot
        /// always remains available as a fallback.
        /// </para>
        /// </remarks>
        public async Task PublishAsync(SnapshotHandle handle)
        {
            if (handle == null || string.IsNullOrEmpty(handle.CountryId)) return;
            if (string.IsNullOrEmpty(handle.ManifestPath) || !File.Exists(handle.ManifestPath)) return;

            string netDir;
            try { netDir = EnsureNetworkCountryDir(handle.CountryId); }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: cannot resolve network dir for '{handle.CountryId}': {ex.Message}");
                return;
            }
            if (string.IsNullOrEmpty(netDir)) return;

            // Serialize publish+pull per country so two concurrent imports (shouldn't happen thanks
            // to the global lock, but be defensive) don't race on the same zip path.
            var gate = _networkGates.GetOrAdd(handle.CountryId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var stamp = handle.StartedUtc.ToString(TimestampFormat);
                var zipName = $"snapshot_{handle.CountryId}_{stamp}.zip";
                var destZip = Path.Combine(netDir, zipName);
                var tempLocalZip = Path.Combine(Path.GetTempPath(), $"{zipName}.tmp_{Guid.NewGuid():N}");

                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // Build the zip locally first — cheaper than streaming over the wire, and keeps
                    // the network path untouched until the archive is fully formed.
                    await Task.Run(() =>
                    {
                        using (var archive = ZipFile.Open(tempLocalZip, ZipArchiveMode.Create))
                        {
                            AddEntryIfExists(archive, handle.ManifestPath);
                            AddEntryIfExists(archive, handle.DwingsSnapshotPath);
                            AddEntryIfExists(archive, handle.RecoSnapshotPath);
                        }
                    }).ConfigureAwait(false);

                    // Atomic replace on the share: copy to temp, delete target, move into place.
                    var tempRemoteZip = destZip + ".tmp_" + Guid.NewGuid().ToString("N");
                    await Task.Run(() => File.Copy(tempLocalZip, tempRemoteZip, overwrite: true)).ConfigureAwait(false);
                    try { if (File.Exists(destZip)) File.Delete(destZip); } catch { /* retry-proof below */ }
                    await Task.Run(() => File.Move(tempRemoteZip, destZip)).ConfigureAwait(false);

                    timer.Stop();
                    LogManager.Info($"[PERF] Snapshot published for {handle.CountryId} in {timer.ElapsedMilliseconds}ms → {destZip} " +
                                    $"({new FileInfo(destZip).Length / 1024} KB)");

                    // Trim the network retention window; keeps disk usage bounded without ever
                    // deleting the zip we just published (the newest one always survives).
                    PurgeOldNetwork(handle.CountryId, GetNetworkRetainCount());
                }
                finally
                {
                    try { if (File.Exists(tempLocalZip)) File.Delete(tempLocalZip); } catch { /* best-effort */ }
                }
            }
            finally
            {
                try { gate.Release(); } catch { }
            }
        }

        /// <summary>
        /// Rate-limited wrapper around <see cref="PullLatestAsync"/>. Safe to call from every
        /// diff entry point — under the <see cref="PullRefreshInterval"/> window it's a no-op,
        /// so rapid view refreshes don't spam the share with directory scans.
        /// </summary>
        public async Task EnsureLatestAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return;

            // Read-then-write on the timestamp map without locking: worst case, two concurrent
            // callers both pass the gate and do a scan — the per-country semaphore in PullLatestAsync
            // still serializes the actual IO, so there's no correctness issue.
            var now = DateTime.UtcNow;
            if (_lastPullUtc.TryGetValue(countryId, out var last) && (now - last) < PullRefreshInterval)
                return;
            _lastPullUtc[countryId] = now;

            try { await PullLatestAsync(countryId).ConfigureAwait(false); }
            catch (Exception ex) { LogManager.Warning($"Snapshot: EnsureLatestAsync failed: {ex.Message}"); }
        }

        /// <summary>
        /// Scans the shared snapshots folder for zips whose stamp isn't present in the local cache,
        /// downloads them, and extracts their contents into the local <c>Snapshots</c> dir so
        /// every read-path API (<see cref="GetPathsForLastRun"/>, <see cref="ListRuns"/>, …)
        /// transparently sees shared runs as if they had been produced locally.
        /// </summary>
        /// <returns>Number of new runs pulled during this invocation.</returns>
        public async Task<int> PullLatestAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return 0;

            string netDir;
            try { netDir = GetNetworkCountrySnapshotDir(countryId); }
            catch { return 0; }
            if (string.IsNullOrEmpty(netDir) || !Directory.Exists(netDir)) return 0;

            string localDir;
            try { localDir = EnsureCountryDir(countryId); }
            catch { return 0; }

            var gate = _networkGates.GetOrAdd(countryId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                int pulled = 0;
                string[] netZips;
                try { netZips = Directory.GetFiles(netDir, $"snapshot_{countryId}_*.zip"); }
                catch { return 0; }

                foreach (var netZip in netZips)
                {
                    // Dedup by filename stamp. The snapshots are named deterministically from the
                    // run's StartedUtc, so if the local manifest with that stamp exists the content
                    // is already extracted — skip without touching the share.
                    var name = Path.GetFileNameWithoutExtension(netZip);
                    var m = SnapshotZipStampRegex.Match(name);
                    if (!m.Success) continue;
                    var stamp = m.Groups["stamp"].Value;
                    var localManifest = Path.Combine(localDir, $"{ManifestPrefix}{stamp}.json");
                    if (File.Exists(localManifest)) continue;

                    try
                    {
                        await PullSingleZipAsync(netZip, localDir).ConfigureAwait(false);
                        pulled++;
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warning($"Snapshot: pull {Path.GetFileName(netZip)} failed: {ex.Message}");
                    }
                }

                if (pulled > 0)
                {
                    // Trim local cache to its own retention. Shared runs we just pulled are now
                    // indistinguishable from runs produced locally — rotation applies to both.
                    try { PurgeOld(countryId, GetRetainCount()); }
                    catch (Exception ex) { LogManager.Warning($"Snapshot: post-pull purge failed: {ex.Message}"); }
                }
                return pulled;
            }
            finally
            {
                try { gate.Release(); } catch { }
            }
        }

        /// <summary>
        /// Downloads one network zip to a temp file, then extracts the manifest + .accdb entries
        /// into <paramref name="localDir"/>. The network zip is treated as immutable — a partially
        /// downloaded temp file never replaces a valid local artefact.
        /// </summary>
        private static async Task PullSingleZipAsync(string netZip, string localDir)
        {
            var tempZip = Path.Combine(Path.GetTempPath(), Path.GetFileName(netZip) + ".tmp_" + Guid.NewGuid().ToString("N"));
            try
            {
                // Stream-copy: File.Copy over SMB can block the thread for tens of seconds on
                // large archives — use async FileStream.CopyToAsync to keep the caller responsive.
                using (var src = new FileStream(netZip, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                using (var dst = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }

                await Task.Run(() =>
                {
                    using (var archive = ZipFile.OpenRead(tempZip))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories
                            var dest = Path.Combine(localDir, entry.Name);
                            // Temp extract + rename so a partial extract can't corrupt the local cache.
                            var tempExtract = dest + ".extract_" + Guid.NewGuid().ToString("N");
                            entry.ExtractToFile(tempExtract, overwrite: true);
                            try
                            {
                                if (File.Exists(dest)) File.Delete(dest);
                                File.Move(tempExtract, dest);
                            }
                            catch
                            {
                                try { if (File.Exists(tempExtract)) File.Delete(tempExtract); } catch { }
                                throw;
                            }
                        }
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* best-effort */ }
            }
        }

        private static void AddEntryIfExists(ZipArchive archive, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            // CompressionLevel.Optimal: .accdb files compress 10-20× (lots of empty pages) so the
            // CPU trade-off is worth the network bandwidth savings.
            archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
        }

        // ──────────────────────────────────────────────────────────────────────────────────────
        // Read path
        // ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Enumerates runs for <paramref name="countryId"/>, newest first. Reads manifests only,
        /// not the underlying .accdb files.
        /// </summary>
        public IReadOnlyList<ImportRunSummary> ListRuns(string countryId, int max = 20)
        {
            var result = new List<ImportRunSummary>();
            string dir;
            try { dir = GetCountrySnapshotDir(countryId); }
            catch { return result; }

            if (!Directory.Exists(dir)) return result;

            try
            {
                var manifests = Directory.EnumerateFiles(dir, $"{ManifestPrefix}*.json")
                    .Select(path => (path, manifest: TryReadManifest(path)))
                    .Where(x => x.manifest != null)
                    .OrderByDescending(x => x.manifest.StartedUtc)
                    .Take(Math.Max(1, max));

                foreach (var (_, m) in manifests)
                    result.Add(ToSummary(m));
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: ListRuns failed for '{countryId}': {ex.Message}");
            }
            return result;
        }

        /// <summary>Convenience accessor: <see cref="ListRuns"/> with max=1.</summary>
        public ImportRunSummary GetLastRun(string countryId) =>
            ListRuns(countryId, 1).FirstOrDefault();

        /// <summary>
        /// Resolves the on-disk paths for a given run — required by the comparison service to
        /// open snapshot DBs read-only.
        /// </summary>
        public SnapshotPaths GetPathsForRun(string countryId, Guid runId)
        {
            string dir;
            try { dir = GetCountrySnapshotDir(countryId); }
            catch { return null; }
            if (!Directory.Exists(dir)) return null;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, $"{ManifestPrefix}*.json"))
                {
                    var m = TryReadManifest(file);
                    if (m == null || m.RunId != runId) continue;
                    return BuildPaths(dir, m);
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: GetPathsForRun failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Paths for the most recent run (any kind), or <c>null</c> if no snapshot exists yet.
        /// Used by the <c>HasRecentActivity</c> flag at view load time.
        /// </summary>
        public SnapshotPaths GetPathsForLastRun(string countryId)
        {
            string dir;
            try { dir = GetCountrySnapshotDir(countryId); }
            catch { return null; }
            if (!Directory.Exists(dir)) return null;

            try
            {
                var latest = Directory.EnumerateFiles(dir, $"{ManifestPrefix}*.json")
                    .Select(TryReadManifest)
                    .Where(m => m != null)
                    .OrderByDescending(m => m.StartedUtc)
                    .FirstOrDefault();
                return latest == null ? null : BuildPaths(dir, latest);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: GetPathsForLastRun failed: {ex.Message}");
                return null;
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────────────
        // Rotation
        // ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Deletes runs beyond the <paramref name="keepCount"/> most recent ones. Safe to call
        /// repeatedly — a run whose manifest exists but whose .accdb copies are already gone
        /// still counts as "a run" and gets its manifest removed.
        /// </summary>
        public void PurgeOld(string countryId, int keepCount)
        {
            if (keepCount < 1) keepCount = 1;
            string dir;
            try { dir = GetCountrySnapshotDir(countryId); }
            catch { return; }
            if (!Directory.Exists(dir)) return;

            try
            {
                var all = Directory.EnumerateFiles(dir, $"{ManifestPrefix}*.json")
                    .Select(path => (path, manifest: TryReadManifest(path)))
                    .Where(x => x.manifest != null)
                    .OrderByDescending(x => x.manifest.StartedUtc)
                    .ToList();

                foreach (var (path, m) in all.Skip(keepCount))
                {
                    TryDelete(Path.Combine(dir, m.AmbreSnapshot ?? string.Empty));
                    TryDelete(Path.Combine(dir, m.DwingsSnapshot ?? string.Empty));
                    TryDelete(Path.Combine(dir, m.RecoSnapshot ?? string.Empty));
                    TryDelete(path);
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: PurgeOld failed: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────────────
        // Internals
        // ──────────────────────────────────────────────────────────────────────────────────────

        internal string GetCountrySnapshotDir(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId))
                throw new ArgumentException("countryId is required", nameof(countryId));
            var root = GetSnapshotsRoot();
            return Path.Combine(root, countryId);
        }

        internal string GetSnapshotsRoot()
        {
            // Parameter override first (lets ops redirect to a larger volume if needed), otherwise
            // sit next to the live DBs so an admin can find them without chasing paths.
            var overridePath = _ofs.GetParameter("SnapshotsDirectory");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
            var data = _ofs.GetParameter("DataDirectory");
            if (string.IsNullOrWhiteSpace(data))
                throw new InvalidOperationException("DataDirectory parameter is not set.");
            return Path.Combine(data, "Snapshots");
        }

        private string EnsureCountryDir(string countryId)
        {
            var d = GetCountrySnapshotDir(countryId);
            Directory.CreateDirectory(d);
            return d;
        }

        private int GetRetainCount()
        {
            try
            {
                var v = _ofs.GetParameter("SnapshotsRetainCount");
                if (int.TryParse(v, out var n) && n > 0) return n;
            }
            catch { }
            return DefaultRetainCount;
        }

        private int GetNetworkRetainCount()
        {
            try
            {
                var v = _ofs.GetParameter("SnapshotsNetworkRetainCount");
                if (int.TryParse(v, out var n) && n > 0) return n;
            }
            catch { }
            return DefaultNetworkRetainCount;
        }

        /// <summary>
        /// Root of the shared snapshots folder: <c>{CountryDatabaseDirectory}\Snapshots</c> by
        /// default. Override via <c>T_Param.NetworkSnapshotsDirectory</c> when ops want to
        /// redirect to a dedicated share.
        /// </summary>
        internal string GetNetworkSnapshotsRoot()
        {
            var overridePath = _ofs.GetParameter("NetworkSnapshotsDirectory");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
            var remote = _ofs.GetParameter("CountryDatabaseDirectory");
            if (string.IsNullOrWhiteSpace(remote))
                throw new InvalidOperationException("CountryDatabaseDirectory parameter is not set.");
            return Path.Combine(remote, "Snapshots");
        }

        internal string GetNetworkCountrySnapshotDir(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId))
                throw new ArgumentException("countryId is required", nameof(countryId));
            return Path.Combine(GetNetworkSnapshotsRoot(), countryId);
        }

        private string EnsureNetworkCountryDir(string countryId)
        {
            var d = GetNetworkCountrySnapshotDir(countryId);
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>
        /// Mirrors <see cref="PurgeOld"/> for the shared folder. Deletes only zip files — we
        /// never touch anything we didn't produce (manifests on the share are inside the zips).
        /// </summary>
        private void PurgeOldNetwork(string countryId, int keepCount)
        {
            if (keepCount < 1) keepCount = 1;
            string dir;
            try { dir = GetNetworkCountrySnapshotDir(countryId); }
            catch { return; }
            if (!Directory.Exists(dir)) return;

            try
            {
                // Order by the timestamp encoded in the filename, not LastWriteTime — the latter
                // can be rewritten by an antivirus touching the file and lie about the real run order.
                var all = Directory.EnumerateFiles(dir, $"snapshot_{countryId}_*.zip")
                    .Select(path =>
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        var match = SnapshotZipStampRegex.Match(name);
                        return (path, stamp: match.Success ? match.Groups["stamp"].Value : null);
                    })
                    .Where(x => x.stamp != null)
                    .OrderByDescending(x => x.stamp, StringComparer.Ordinal)
                    .ToList();

                foreach (var (path, _) in all.Skip(keepCount))
                    TryDelete(path);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: PurgeOldNetwork failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts the file path from the OLE DB connection string returned by
        /// <see cref="OfflineFirstService.GetCountryConnectionString"/>. Kept as a string-parse
        /// to avoid opening the connection just to read its path.
        /// </summary>
        private string GetLocalReconciliationPath(string countryId)
        {
            try
            {
                var cs = _ofs.GetCountryConnectionString(countryId);
                if (string.IsNullOrWhiteSpace(cs)) return null;
                var m = DataSourceRegex.Match(cs);
                return m.Success ? m.Groups["path"].Value.Trim() : null;
            }
            catch { return null; }
        }

        private static async Task SafeCopyAsync(string source, string destination)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination)) return;
            try
            {
                // File.Copy is synchronous at the OS level; wrap in Task.Run so multiple copies
                // can overlap and the caller's UI thread stays responsive.
                await Task.Run(() => File.Copy(source, destination, overwrite: true)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot: copy {Path.GetFileName(source)} failed: {ex.Message}");
            }
        }

        private static Manifest TryReadManifest(string path)
        {
            try { return ReadManifest(path); }
            catch { return null; }
        }

        private static Manifest ReadManifest(string path)
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<Manifest>(json);
        }

        private static void WriteManifest(string path, Manifest manifest)
        {
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort */ }
        }

        private static SnapshotPaths BuildPaths(string countryDir, Manifest m)
        {
            string Resolve(string name) => string.IsNullOrEmpty(name) ? null : Path.Combine(countryDir, name);
            return new SnapshotPaths
            {
                RunId = m.RunId,
                CountryId = m.CountryId,
                StartedUtc = m.StartedUtc,
                AmbrePath = Resolve(m.AmbreSnapshot),
                DwingsPath = Resolve(m.DwingsSnapshot),
                RecoPath = Resolve(m.RecoSnapshot),
            };
        }

        private static ImportRunSummary ToSummary(Manifest m)
        {
            Enum.TryParse<RunKind>(m.Kind, true, out var kind);
            return new ImportRunSummary
            {
                ImportRunId = m.RunId,
                CountryId = m.CountryId,
                Kind = kind,
                StartedUtc = m.StartedUtc,
                EndedUtc = m.EndedUtc,
                TriggeredBy = m.TriggeredBy,
                NewCount = m.NewCount,
                UpdatedCount = m.UpdatedCount,
                ArchivedCount = m.ArchivedCount,
                RulesAppliedCount = 0, // Not tracked in snapshot mode (derived on demand).
                Success = m.Success,
                ErrorMessage = m.ErrorMessage,
            };
        }

        /// <summary>
        /// On-disk manifest shape. Kept minimal and versionable; new fields must default to
        /// zero/null so older manifests still deserialise cleanly.
        /// </summary>
        internal sealed class Manifest
        {
            public Guid RunId { get; set; }
            public string CountryId { get; set; }
            public string Kind { get; set; }
            public DateTime StartedUtc { get; set; }
            public DateTime? EndedUtc { get; set; }
            public string TriggeredBy { get; set; }
            public string AmbreSnapshot { get; set; }
            public string DwingsSnapshot { get; set; }
            public string RecoSnapshot { get; set; }
            public int NewCount { get; set; }
            public int UpdatedCount { get; set; }
            public int ArchivedCount { get; set; }
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
        }
    }

    /// <summary>
    /// Returned by <see cref="SnapshotService.BeginAsync"/>. Opaque token to be threaded through
    /// the import pipeline and passed back to <see cref="SnapshotService.CompleteAsync"/>.
    /// </summary>
    public sealed class SnapshotHandle
    {
        public Guid RunId { get; internal set; }
        public string CountryId { get; internal set; }
        public RunKind Kind { get; internal set; }
        public DateTime StartedUtc { get; internal set; }
        public string AmbreSnapshotPath { get; internal set; }
        public string DwingsSnapshotPath { get; internal set; }
        public string RecoSnapshotPath { get; internal set; }
        public string ManifestPath { get; internal set; }
    }

    /// <summary>
    /// Paths for a persisted snapshot run — used by <see cref="SnapshotComparisonService"/> to
    /// open the snapshot DBs in read-only mode.
    /// </summary>
    public sealed class SnapshotPaths
    {
        public Guid RunId { get; internal set; }
        public string CountryId { get; internal set; }
        public DateTime StartedUtc { get; internal set; }
        public string AmbrePath { get; internal set; }
        public string DwingsPath { get; internal set; }
        public string RecoPath { get; internal set; }
    }
}
