using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DAO = Microsoft.Office.Interop.Access.Dao;   // Référence COM « Microsoft DAO 3.6 Object Library »

namespace RecoTool.Services.Helpers
{
    /*=====================================================================*
     *  1️⃣  AccessLinkHelper – version DAO‑only                           *
     *=====================================================================*/
    internal sealed class AccessLinkHelper : IDisposable
    {
        #region fields & ctor

        private readonly string _mainMdbPath;      // fichier .mdb/.accdb principal
        private readonly DAO.DBEngine _dbEngine;   // moteur DAO (pas d’Access.Application)
        private readonly DAO.Database _daoDatabase; // base ouverte
        private bool _disposed;

        internal AccessLinkHelper(string mainMdbPath)
        {
            if (string.IsNullOrWhiteSpace(mainMdbPath))
                throw new ArgumentException("Chemin du fichier principal manquant.", nameof(mainMdbPath));

            _mainMdbPath = mainMdbPath;

            // ---------- 1️⃣  DAO Engine (COM) ----------
            // DBEngineClass est le ProgID « DAO.DBEngine.36 » – crée un moteur DAO pur.
            _dbEngine = new DAO.DBEngineClass();

            // ---------- 2️⃣  Ouvrir la base ----------
            // readOnly = false, exclusive = false
            _daoDatabase = _dbEngine.Workspaces[0].OpenDatabase(
                _mainMdbPath,
                false,
                false,
                string.Empty);
        }
        #endregion

        #region extraction d’une référence externe (regex ultra‑simple)

        //   [chemin\vers\base.ext].NomTable
        private static readonly Regex _refRegex = new Regex(
            @"\[(?<path>[^\]]+)\]\.(?<table>\w+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public sealed class ExtRef
        {
            public string FullMatch { get; set; } = default!;
            public string Path { get; set; } = default!;
            public string Table { get; set; } = default!;
        }

        /// <summary>
        /// Retourne toutes les références externes trouvées dans le texte SQL.
        /// </summary>
        public static IReadOnlyList<ExtRef> Extract(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return Array.Empty<ExtRef>();

            var matches = _refRegex.Matches(sql);
            var list = new List<ExtRef>(matches.Count);
            foreach (Match m in matches)
            {
                list.Add(new ExtRef
                {
                    FullMatch = m.Value,
                    Path = m.Groups["path"].Value,
                    Table = m.Groups["table"].Value
                });
            }
            return list;
        }
        #endregion

        #region création / vérif. d’une table liée (DAO)

        /// <summary>
        /// Crée la table liée si elle n’existe pas et renvoie le nom local.
        /// </summary>
        public async Task<string> EnsureLinkedTableAsync(
            string externalPath,
            string externalTable,
            CancellationToken ct = default)
        {
            // ----- a) nom local déterministe -----
            string baseName = System.IO.Path.GetFileNameWithoutExtension(externalPath);
            string localName = $"LINKED_{baseName}_{externalTable}"
                .Replace("-", "_")
                .Replace(".", "_")
                .ToUpperInvariant();

            // ----- b) déjà présente ? -----
            if (TableExists(localName))
                return localName;

            // ----- c) création du TableDef -----
            await Task.Run(() =>
            {
                // DAO n’est pas thread‑safe – le Task.Run s’exécute sur un thread du pool,
                // donc on s’assure d’être en STA (voir remarque plus bas).
                var tdf = _daoDatabase.CreateTableDef(localName);

                // Si le chemin contient des espaces, on l’entoure de guillemets doubles.
                string escapedPath = externalPath.Contains(' ')
                    ? $"\"{externalPath}\""
                    : externalPath;

                // Connect string – on utilise le provider ACE/JET.
                tdf.Connect = $"MS Access;DATABASE={escapedPath}";
                tdf.SourceTableName = externalTable;

                _daoDatabase.TableDefs.Append(tdf);
                _daoDatabase.TableDefs.Refresh();
            }, ct).ConfigureAwait(false);

            return localName;
        }

        /// <summary>
        /// Vérifie l’existence d’un TableDef (ignore les tables système).
        /// </summary>
        private bool TableExists(string localName)
        {
            foreach (DAO.TableDef td in _daoDatabase.TableDefs)
            {
                if (string.Equals(td.Name, localName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        #endregion

        #region substitution dans la requête

        /// <summary>
        /// Remplace chaque occurrence exacte « [chemin].Table » par le nom local.
        /// </summary>
        public static string ReplaceInQuery(
            string originalQuery,
            IReadOnlyDictionary<string, string> map) // key = FullMatch, value = localName
        {
            if (map == null || map.Count == 0) return originalQuery;

            var pattern = string.Join("|",
                map.Keys
                   .Select(k => Regex.Escape(k))
                   .OrderByDescending(k => k.Length));

            return Regex.Replace(originalQuery, pattern,
                m => $"[{map[m.Value]}]",
                RegexOptions.IgnoreCase);
        }
        #endregion

        #region orchestration publique (exécutée depuis le manager)

        /// <summary>
        /// 1️⃣ extrait les références externes,
        /// 2️⃣ crée les tables liées si besoin,
        /// 3️⃣ renvoie la requête où chaque référence est remplacée.
        /// </summary>
        public async Task<string> PrepareQueryAsync(
            string originalQuery,
            CancellationToken ct = default)
        {
            var externals = Extract(originalQuery);
            if (externals.Count == 0) return originalQuery;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ext in externals)
            {
                if (!map.ContainsKey(ext.FullMatch))
                {
                    string localName = await EnsureLinkedTableAsync(
                        ext.Path,
                        ext.Table,
                        ct).ConfigureAwait(false);
                    map[ext.FullMatch] = localName;
                }
            }

            return ReplaceInQuery(originalQuery, map);
        }
        #endregion

        #region Dispose & libération COM

        public void Dispose()
        {
            if (_disposed) return;

            // 1️⃣ fermer la base DAO
            try { _daoDatabase?.Close(); } catch { /* ignore */ }

            // 2️⃣ libérer les COM objects DAO
            if (_daoDatabase != null) Marshal.ReleaseComObject(_daoDatabase);
            if (_dbEngine != null) Marshal.ReleaseComObject(_dbEngine);

            GC.Collect();
            GC.WaitForPendingFinalizers();

            _disposed = true;
        }
        #endregion
    }

    /*=====================================================================*
     *  2️⃣  AccessLinkManager – cache global d’instances (inchangé)      *
     *=====================================================================*/
    internal static class AccessLinkManager
    {
        private static readonly ConcurrentDictionary<string, CacheEntry> _cache
            = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        public sealed class CacheEntry
        {
            public AccessLinkHelper Helper { get; }
            private int _refCount;

            public CacheEntry(AccessLinkHelper helper)
            {
                Helper = helper ?? throw new ArgumentNullException(nameof(helper));
                _refCount = 1;
            }

            public int Increment() => Interlocked.Increment(ref _refCount);
            public int Decrement() => Interlocked.Decrement(ref _refCount);
        }

        public static AccessLinkHandle GetHandle(string mainMdbPath)
        {
            if (string.IsNullOrWhiteSpace(mainMdbPath))
                throw new ArgumentException("Chemin du fichier principal manquant.", nameof(mainMdbPath));

            var entry = _cache.GetOrAdd(
                mainMdbPath,
                path => new CacheEntry(new AccessLinkHelper(path)));

            entry?.Increment();

            return new AccessLinkHandle(entry, mainMdbPath);
        }

        private static void Release(string path, CacheEntry entry)
        {
            if (entry == null) return;
            if (entry.Decrement() <= 0)
            {
                _cache.TryRemove(path, out _);
                entry.Helper.Dispose();
            }
        }

        public static void DisposeAll()
        {
            foreach (var kvp in _cache)
                kvp.Value.Helper.Dispose();

            _cache.Clear();
        }

        public sealed class AccessLinkHandle : IDisposable
        {
            private readonly CacheEntry _entry;
            private readonly string _path;
            private bool _disposed;

            internal AccessLinkHandle(CacheEntry entry, string path)
            {
                _entry = entry;
                _path = path;
            }

            public AccessLinkHelper Helper => _entry.Helper;

            public void Dispose()
            {
                if (_disposed) return;
                Release(_path, _entry);
                _disposed = true;
            }
        }
    }
}