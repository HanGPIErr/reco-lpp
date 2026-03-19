using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.OleDb;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using OfflineFirstAccess.Data;
using OfflineFirstAccess.Models;
using RecoTool.Helpers;
using RecoTool.Models;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.Infrastructure.IO;
using RecoTool.Services;

namespace RecoTool.Windows
{
    public partial class ReconciliationImportWindow : Window, INotifyPropertyChanged
    {
        #region Fields & Properties

        private readonly OfflineFirstService _offlineFirstService;
        private CancellationTokenSource _cts;

        private string _mappingFilePath;
        private string _dataFilePath;
        private bool _isImporting;

        public event PropertyChangedEventHandler PropertyChanged;

        public string MappingFilePath
        {
            get => _mappingFilePath;
            set
            {
                _mappingFilePath = value;
                OnPropertyChanged(nameof(MappingFilePath));
                OnFilePathChanged();
            }
        }

        public string DataFilePath
        {
            get => _dataFilePath;
            set
            {
                _dataFilePath = value;
                OnPropertyChanged(nameof(DataFilePath));
                OnFilePathChanged();
            }
        }

        public bool IsImporting
        {
            get => _isImporting;
            set
            {
                _isImporting = value;
                OnPropertyChanged(nameof(IsImporting));
                UpdateButtonStates();
            }
        }

        #endregion

        #region Nested Classes

        /// <summary>Configuration extraite du fichier de mapping</summary>
        private class MappingConfig
        {
            public string BookingCode { get; set; }
            public int PivotStartRow { get; set; }
            public int ReceivableStartRow { get; set; }

            public Dictionary<string, string> PivotLetterToDest { get; set; }
            public Dictionary<string, object> PivotConstants { get; set; }

            public Dictionary<string, string> RecvLetterToDest { get; set; }
            public Dictionary<string, object> RecvConstants { get; set; }

            public MappingConfig()
            {
                PivotLetterToDest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                PivotConstants = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                RecvLetterToDest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                RecvConstants = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Index Event_Num → ID pour résolution des IDs</summary>
        private class DatabaseIndices
        {
            public Dictionary<string, string> EventNumToId { get; set; }

            public DatabaseIndices()
            {
                EventNumToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Statistiques d'import</summary>
        private class ImportStats
        {
            public int TotalRows { get; set; }
            public int Resolved { get; set; }
            public int Unresolved { get; set; }
            public int CommentsWithText { get; set; }
            public int FirstClaimCount { get; set; }
            public int LastClaimCount { get; set; }
            public List<(string ExcelId, string Comments, string FirstClaim, string LastClaim)> UnresolvedDetails { get; set; }

            public ImportStats()
            {
                UnresolvedDetails = new List<(string, string, string, string)>();
            }
        }

        #endregion

        #region Constructors

        public ReconciliationImportWindow()
        {
            _offlineFirstService = App.ServiceProvider?.GetRequiredService<OfflineFirstService>();
            InitializeComponent();
            DataContext = this;
            UpdateButtonStates();
        }

        public ReconciliationImportWindow(OfflineFirstService offlineFirstService)
        {
            _offlineFirstService = offlineFirstService ?? App.ServiceProvider?.GetRequiredService<OfflineFirstService>();
            InitializeComponent();
            DataContext = this;
            UpdateButtonStates();
        }

        #endregion

        #region UI Event Handlers

        private void BrowseMappingButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ofd = new OpenFileDialog
                {
                    Title = "Select mapping Excel",
                    Filter = "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls|All Files (*.*)|*.*",
                    Multiselect = false
                };

                if (ofd.ShowDialog() == true)
                {
                    MappingFilePath = ofd.FileName;
                    MappingFileTextBox.Text = MappingFilePath;
                    UpdateFileInfo(MappingFilePath, FileInfoMappingText);
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error selecting mapping file: {ex.Message}");
            }
        }

        private void BrowseDataButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ofd = new OpenFileDialog
                {
                    Title = "Select reconciliation data Excel",
                    Filter = "Excel Files (*.xlsx;*.xls)|*.xlsx;*.xls|All Files (*.*)|*.*",
                    Multiselect = false
                };

                if (ofd.ShowDialog() == true)
                {
                    DataFilePath = ofd.FileName;
                    DataFileTextBox.Text = DataFilePath;
                    UpdateFileInfo(DataFilePath, FileInfoDataText);
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error selecting data file: {ex.Message}");
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsImporting) return;
            ImportButton.IsEnabled = false;
            await StartImportAsync();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsImporting)
            {
                try
                {
                    _cts?.Cancel();
                    LogMessage("Cancellation requested...");
                }
                catch { }
            }
            else
            {
                Close();
            }
        }

        private void UpdateButtonStates()
        {
            var ready = !string.IsNullOrEmpty(MappingFilePath) &&
                       !string.IsNullOrEmpty(DataFilePath) &&
                       !IsImporting;
            ImportButton.IsEnabled = ready;
            CancelButton.Content = IsImporting ? "Cancel" : "Close";
        }

        private void OnFilePathChanged()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(MappingFilePath))
                parts.Add($"Mapping: {Path.GetFileName(MappingFilePath)}");
            if (!string.IsNullOrEmpty(DataFilePath))
                parts.Add($"Data: {Path.GetFileName(DataFilePath)}");

            StatusSummaryText.Text = parts.Count > 0
                ? string.Join(" | ", parts)
                : "Select mapping and data files";
            UpdateButtonStates();
        }

        private void UpdateFileInfo(string path, System.Windows.Controls.TextBlock target)
        {
            try
            {
                var fi = new FileInfo(path);
                target.Text = $"File: {fi.Name} ({(fi.Length / 1024).ToString("N0", CultureInfo.InvariantCulture)} KB) - " +
                             $"Modified: {fi.LastWriteTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}";
            }
            catch
            {
                target.Text = "";
            }
        }

        #endregion

        #region Import Orchestration

        private async Task StartImportAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(MappingFilePath) || string.IsNullOrEmpty(DataFilePath))
                {
                    ShowWarning("Please select both mapping and data files.");
                    return;
                }

                IsImporting = true;
                _cts = new CancellationTokenSource();
                UpdateProgress(0, "Starting import...");
                LogMessage("Starting reconciliation import...");

                // Étape 1: Validation des fichiers
                ValidateExcelFiles();
                _cts.Token.ThrowIfCancellationRequested();

                // Étape 2: Parsing du mapping
                UpdateProgress(10, "Parsing mapping configuration...");
                var mappingConfig = await ParseMappingConfigAsync();
                _cts.Token.ThrowIfCancellationRequested();

                // Étape 3: Lecture des données Excel
                UpdateProgress(30, "Reading Excel data...");
                var excelData = await ReadExcelDataAsync(mappingConfig);
                _cts.Token.ThrowIfCancellationRequested();

                if (excelData.Count == 0)
                {
                    UpdateProgress(100, "Nothing to import");
                    LogMessage("No data rows found. Nothing to import.");
                    MessageBox.Show("No data rows found.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    return;
                }

                // Étape 4: Construction des indices de base de données
                UpdateProgress(48, "Building database lookup indices...");
                var dbIndices = await BuildDatabaseIndicesAsync();
                _cts.Token.ThrowIfCancellationRequested();

                // Étape 5: Transformation et résolution des IDs
                UpdateProgress(50, "Transforming data...");
                var (transformedData, stats) = TransformAndResolveData(excelData, dbIndices);
                _cts.Token.ThrowIfCancellationRequested();

                LogImportStats(stats);
                ExportUnresolvedDetailsIfNeeded(stats);

                if (transformedData.Count == 0)
                    throw new InvalidOperationException("No valid rows to import (all missing IDs).");

                // Étape 6: Application des changements en base
                UpdateProgress(92, "Applying changes to database...");
                int updatedCount = await ApplyChangesToDatabaseAsync(transformedData);
                _cts.Token.ThrowIfCancellationRequested();

                // Étape 7: Publication vers le réseau
                await PublishToNetworkAsync();

                // Finalisation
                UpdateProgress(100, "Completed");
                LogMessage($"Import completed. Updated rows: {updatedCount:N0}");
                MessageBox.Show($"Import completed successfully. Rows updated: {updatedCount:N0}",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                UpdateProgress(0, "Import canceled");
                LogMessage("Import canceled by user");
            }
            catch (Exception ex)
            {
                LogMessage($"Error during import: {ex.Message}", true);
                ShowError($"Error during import: {ex.Message}");
            }
            finally
            {
                IsImporting = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        #endregion

        #region Import Steps

        private void ValidateExcelFiles()
        {
            if (!ExcelHelper.ValidateExcelFormat(MappingFilePath) ||
                !ExcelHelper.ValidateExcelFormat(DataFilePath))
            {
                throw new InvalidOperationException("Invalid Excel file format. Please select .xlsx or .xls files.");
            }
        }

        private async Task<MappingConfig> ParseMappingConfigAsync()
        {
            return await Task.Run(() =>
            {
                LogMessage("Opening mapping Excel and parsing mapping template...");
                var config = new MappingConfig();

                using (var mappingExcel = new ExcelHelper())
                {
                    mappingExcel.OpenFile(MappingFilePath);
                    var currentBooking = _offlineFirstService?.CurrentCountryId;

                    if (string.IsNullOrWhiteSpace(currentBooking))
                        throw new InvalidOperationException("Aucun pays/booking sélectionné. Veuillez sélectionner un pays dans l'application.");

                    // Trouver la feuille contenant le mapping pour le booking actuel
                    var mappingRow = FindMappingRowForBooking(mappingExcel, currentBooking);
                    if (mappingRow == null)
                        throw new InvalidOperationException($"Aucune ligne de mapping trouvée pour le booking '{currentBooking}'.");

                    // Parser la configuration
                    ParseMappingRow(mappingRow, config);
                }

                LogMappingConfiguration(config);
                return config;
            });
        }

        private Dictionary<string, object> FindMappingRowForBooking(ExcelHelper mappingExcel, string currentBooking)
        {
            var sheetNames = mappingExcel.GetSheetNames();
            var perSheetBookings = new Dictionary<string, List<string>>();

            foreach (var sheetName in sheetNames)
            {
                try
                {
                    var rows = mappingExcel.ReadSheetData(sheetName: sheetName, importFields: null, startRow: 2);
                    if (rows == null || rows.Count == 0) continue;

                    var bookings = rows
                        .Select(r => GetRowValueByPrefix(r, "Booking"))
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    perSheetBookings[sheetName] = bookings;

                    var candidate = rows.FirstOrDefault(r =>
                        string.Equals(GetRowValueByPrefix(r, "Booking"), currentBooking, StringComparison.OrdinalIgnoreCase));

                    if (candidate != null)
                    {
                        LogMessage($"Selected mapping row for booking '{currentBooking}' in sheet '{sheetName}'.");
                        return candidate;
                    }
                }
                catch { /* ignore and continue scanning */ }
            }

            var details = string.Join(" | ", perSheetBookings.Select(kv =>
                $"{kv.Key}: [{string.Join(", ", kv.Value)}]"));
            throw new InvalidOperationException(
                $"Aucune ligne de mapping trouvée pour le booking '{currentBooking}'. Bookings disponibles par feuille: {details}");
        }

        private void ParseMappingRow(Dictionary<string, object> mappingRow, MappingConfig config)
        {
            config.BookingCode = GetRowValueByPrefix(mappingRow, "Booking");
            config.PivotStartRow = GetIntValueByPrefix(mappingRow, "Pivot Starting", 2);
            config.ReceivableStartRow = GetIntValueByPrefix(mappingRow, "Receivable Starting", 2);

            // Parse Pivot mappings (Action retiré - basé sur couleur Comments uniquement)
            AssignMapping(mappingRow, "Pivot Comment", "Comments", false, config);
            AssignMapping(mappingRow, "Pivot KPI", "KPI", false, config);
            AssignMapping(mappingRow, "Pivot RISKY ITEM", "RiskyItem", false, config);
            AssignMapping(mappingRow, "Pivot REASON NON RISKY", "ReasonNonRisky", false, config);

            // Parse Receivable mappings (Action retiré - basé sur couleur Comments uniquement)
            AssignMapping(mappingRow, "Receivable Comment", "Comments", true, config);
            AssignMapping(mappingRow, "Receivable KPI", "KPI", true, config);
            AssignMapping(mappingRow, "Receivable 1ST CLAIM", "FirstClaimDate", true, config);
            AssignMapping(mappingRow, "Receivable LAST CLAIM", "LastClaimDate", true, config);
            AssignMapping(mappingRow, "Receivable RISKY ITEM", "RiskyItem", true, config);
            AssignMapping(mappingRow, "Receivable REASON NON RISKY", "ReasonNonRisky", true, config);
        }

        private void AssignMapping(Dictionary<string, object> row, string prefix, string destination,
            bool isReceivable, MappingConfig config)
        {
            var value = GetRowValueByPrefix(row, prefix);
            if (string.IsNullOrWhiteSpace(value)) return;

            value = value.Trim();

            // Détection colonne lettre vs constante
            if (value.Length == 1 && char.IsLetter(value[0]))
            {
                var letter = value.ToUpperInvariant();
                var letterToDest = isReceivable ? config.RecvLetterToDest : config.PivotLetterToDest;
                letterToDest[letter] = destination;
            }
            else
            {
                // Constante
                var constants = isReceivable ? config.RecvConstants : config.PivotConstants;
                constants[destination] = value;
            }
        }

        private async Task<Dictionary<string, Dictionary<string, object>>> ReadExcelDataAsync(MappingConfig config)
        {
            return await Task.Run(() =>
            {
                var allById = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

                using (var dataExcel = new ExcelHelper())
                {
                    dataExcel.OpenFile(DataFilePath);

                    // Lecture PIVOT - Capturer couleur de Comments (pour Action)
                    var colorFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Comments" };
                    var pivotRows = dataExcel.ReadSheetByColumns("PIVOT", config.PivotLetterToDest,
                        config.PivotStartRow, idColumnLetter: "A", colorForDestinations: colorFields);

                    ProcessSheetRows("PIVOT", pivotRows, config.PivotConstants, allById, overrideExisting: false);

                    // Lecture RECEIVABLE - Capturer couleur de Comments (pour Action)
                    var recvRows = dataExcel.ReadSheetByColumns("RECEIVABLE", config.RecvLetterToDest,
                        config.ReceivableStartRow, idColumnLetter: "A", colorForDestinations: colorFields);

                    ProcessSheetRows("RECEIVABLE", recvRows, config.RecvConstants, allById, overrideExisting: true);
                }

                LogMessage($"Data rows read (Pivot+Receivable). Unique IDs: {allById.Count:N0}");
                return allById;
            });
        }

        private void ProcessSheetRows(string sheetName, List<Dictionary<string, object>> rows,
            Dictionary<string, object> constants, Dictionary<string, Dictionary<string, object>> allById,
            bool overrideExisting)
        {
            // Sample diagnostics
            if (rows.Count > 0)
                LogSampleRow(sheetName, rows[0]);

            // Merge dans allById
            foreach (var row in rows)
            {
                var id = row.TryGetValue("ID", out var idv) ? idv?.ToString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                // Skip duplicates si on n'override pas
                if (!overrideExisting && allById.ContainsKey(id)) continue;

                row["ID"] = id;
                if (!allById.TryGetValue(id, out var agg))
                {
                    agg = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["ID"] = id };
                    allById[id] = agg;
                }

                // Copier toutes les valeurs
                foreach (var kv in row)
                {
                    if (!kv.Key.Equals("ID", StringComparison.OrdinalIgnoreCase))
                    {
                        if (overrideExisting || !agg.ContainsKey(kv.Key) || agg[kv.Key] == null)
                            agg[kv.Key] = kv.Value;
                    }
                }

                // Appliquer les constantes du mapping
                foreach (var c in constants)
                {
                    if (overrideExisting || !agg.ContainsKey(c.Key) || agg[c.Key] == null)
                        agg[c.Key] = c.Value;
                }
            }
        }

        private async Task<DatabaseIndices> BuildDatabaseIndicesAsync()
        {
            return await Task.Run(async () =>
            {
                LogMessage("Building Event_Num → ID index from AMBRE (T_Data_Ambre)...");
                var indices = new DatabaseIndices();

                if (_offlineFirstService == null)
                    throw new InvalidOperationException("OfflineFirstService is not available.");

                var ambrePath = _offlineFirstService.GetLocalAmbreDatabasePath();
                if (string.IsNullOrWhiteSpace(ambrePath) || !File.Exists(ambrePath))
                    throw new InvalidOperationException("Local AMBRE database not found. Please refresh AMBRE for the current country.");

                var ambreConnStr = DbConn.AceConn(ambrePath);
                using (var conn = new OleDbConnection(ambreConnStr))
                {
                    await conn.OpenAsync();
                    var sql = "SELECT ID, Event_Num FROM [T_Data_Ambre]";

                    using (var cmd = new OleDbCommand(sql, conn))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var id = reader["ID"]?.ToString();
                            if (string.IsNullOrWhiteSpace(id)) continue;

                            var ev = reader["Event_Num"];
                            var eventNum = ev == null || ev is DBNull ? null : ev.ToString()?.Trim();
                            if (string.IsNullOrWhiteSpace(eventNum)) continue;

                            // Index exact et normalisé
                            if (!indices.EventNumToId.ContainsKey(eventNum))
                                indices.EventNumToId[eventNum] = id;

                            var normalized = NormalizeKey(eventNum);
                            if (normalized != null && !indices.EventNumToId.ContainsKey(normalized))
                                indices.EventNumToId[normalized] = id;
                        }
                    }
                }

                LogMessage($"Built Event_Num index: {indices.EventNumToId.Count:N0} keys");
                return indices;
            });
        }

        private (List<Dictionary<string, object>> TransformedData, ImportStats Stats)
            TransformAndResolveData(Dictionary<string, Dictionary<string, object>> excelData, DatabaseIndices dbIndices)
        {
            var stats = new ImportStats { TotalRows = excelData.Count };
            var toApply = new List<Dictionary<string, object>>(excelData.Count);
            var mappingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Action", "KPI", "ReasonNonRisky", "IncidentType", "RiskyItem"
            };

            int processed = 0;
            foreach (var rawRow in excelData.Values)
            {
                _cts.Token.ThrowIfCancellationRequested();

                if (!rawRow.TryGetValue("ID", out var idVal) || idVal == null ||
                    string.IsNullOrWhiteSpace(idVal.ToString()))
                {
                    if ((processed % 100) == 0)
                        LogMessage($"Skipping row without ID (row #{processed + 1}).", isError: true);
                    processed++;
                    continue;
                }

                var rec = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in rawRow)
                {
                    var key = kv.Key?.Trim();
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    object norm = NormalizeValueForReconciliation(key, kv.Value);
                    rec[key] = norm;

                    // Avertissement pour valeurs non mappées
                    if (mappingKeys.Contains(key))
                        WarnUnmappedValue(key, kv.Value, norm);
                }

                // Tracking pour stats
                TrackFieldPresence(rec, stats);

                // Inférence de l'Action par couleur
                InferActionFromCellColor(rawRow, rec);

                // Suppression des clés de couleur temporaires
                RemoveColorKeys(rec);

                // Résolution de l'ID réel
                string resolvedId = TryResolveId(rec, rawRow, dbIndices, stats);
                if (!string.IsNullOrWhiteSpace(resolvedId))
                    rec["ID"] = resolvedId;

                rec["ID"] = rec["ID"]?.ToString();
                toApply.Add(rec);

                processed++;
                if ((processed % 250) == 0)
                {
                    int pct = 50 + (int)Math.Round(processed * 40.0 / excelData.Count);
                    if (pct > 90) pct = 90;
                    UpdateProgress(pct, $"Transforming... {processed:N0}/{excelData.Count:N0}");
                }
            }

            return (toApply, stats);
        }

        private void TrackFieldPresence(Dictionary<string, object> rec, ImportStats stats)
        {
            try
            {
                if (rec.TryGetValue("Comments", out var cmtVal) &&
                    !string.IsNullOrWhiteSpace(cmtVal?.ToString()))
                    stats.CommentsWithText++;

                if (rec.TryGetValue("FirstClaimDate", out var fcVal) && fcVal != null && fcVal != DBNull.Value)
                    stats.FirstClaimCount++;

                if (rec.TryGetValue("LastClaimDate", out var lcVal) && lcVal != null && lcVal != DBNull.Value)
                    stats.LastClaimCount++;
            }
            catch { }
        }

        private void InferActionFromCellColor(Dictionary<string, object> rawRow, Dictionary<string, object> rec)
        {
            try
            {
                // Action basée uniquement sur la couleur de Comments
                int? commentsColor = TryGetOleColor(rawRow, "Comments__Color");
                var colorInferred = InferActionFromColor(commentsColor);

                if (colorInferred != null)
                {
                    rec["Action"] = colorInferred.Value;
                    LogMessage($"Action inferred from Comments cell color -> {(ActionType)colorInferred.Value}");
                }
            }
            catch { /* best effort inference */ }
        }

        private void RemoveColorKeys(Dictionary<string, object> rec)
        {
            var colorKeys = rec.Keys.Where(k => k.EndsWith("__Color", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var ck in colorKeys)
                rec.Remove(ck);
        }

        private string TryResolveId(Dictionary<string, object> rec, Dictionary<string, object> rawRow,
            DatabaseIndices dbIndices, ImportStats stats)
        {
            var eventNum = rec.TryGetValue("ID", out var iv) ? iv?.ToString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(eventNum))
            {
                stats.Unresolved++;
                return null;
            }

            // Recherche directe par Event_Num
            if (dbIndices.EventNumToId.TryGetValue(eventNum, out var id))
            {
                stats.Resolved++;
                return id;
            }

            // Tentative avec normalisation
            var normalized = NormalizeKey(eventNum);
            if (normalized != null && dbIndices.EventNumToId.TryGetValue(normalized, out id))
            {
                stats.Resolved++;
                return id;
            }

            // Non résolu
            stats.Unresolved++;
            CollectUnresolvedDetails(eventNum, rawRow, stats);
            return null;
        }

        private void CollectUnresolvedDetails(string excelId, Dictionary<string, object> rawRow, ImportStats stats)
        {
            try
            {
                string commentsText = rawRow.TryGetValue("Comments", out var cv) ? (cv?.ToString() ?? string.Empty) : string.Empty;
                string firstClaim = rawRow.TryGetValue("FirstClaimDate", out var fcv) ? (FormatDateDdMmYyyy(fcv) ?? string.Empty) : string.Empty;
                string lastClaim = rawRow.TryGetValue("LastClaimDate", out var lcv) ? (FormatDateDdMmYyyy(lcv) ?? string.Empty) : string.Empty;
                stats.UnresolvedDetails.Add((excelId, commentsText, firstClaim, lastClaim));
            }
            catch { }
        }

        private async Task<int> ApplyChangesToDatabaseAsync(List<Dictionary<string, object>> transformedData)
        {
            LogMessage("Preparing database update into T_Reconciliation (update-only)...");

            if (_offlineFirstService == null)
                throw new InvalidOperationException("OfflineFirstService is not available.");

            var connStr = _offlineFirstService.GetCurrentLocalConnectionString();
            var syncCfg = new SyncConfiguration
            {
                LocalDatabasePath = connStr,
                PrimaryKeyColumn = "ID",
                LastModifiedColumn = "LastModified",
                IsDeletedColumn = "IsDeleted"
            };

            var provider = await AccessDataProvider.CreateAsync(connStr, syncCfg);

            // Filtrer pour ne garder que les IDs existants (update-only)
            var idList = transformedData
                .Select(r => r.TryGetValue("ID", out var v) ? v?.ToString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existingRecords = await provider.GetRecordsByIds("T_Reconciliation", idList);
            var existingIds = new HashSet<string>(
                existingRecords.Select(rec => rec.TryGetValue("ID", out var v) ? v?.ToString() : null)
                              .Where(s => !string.IsNullOrWhiteSpace(s)),
                StringComparer.OrdinalIgnoreCase);

            var beforeCount = transformedData.Count;
            var toApply = transformedData.Where(r => existingIds.Contains(r["ID"]?.ToString())).ToList();
            var dropped = beforeCount - toApply.Count;

            // Timestamp pour update
            var now = DateTime.Now;
            foreach (var rec in toApply)
                rec["LastModified"] = now;

            LogMessage($"Update-only: {toApply.Count:N0} existing IDs; skipped {dropped:N0} non-existing IDs (no inserts).");

            _cts.Token.ThrowIfCancellationRequested();
            UpdateProgress(95, "Applying changes (update-only)...");

            await provider.ApplyChangesAsync("T_Reconciliation", toApply);

            return toApply.Count;
        }

        private async Task PublishToNetworkAsync()
        {
            try
            {
                var countryId = _offlineFirstService?.CurrentCountryId;
                if (string.IsNullOrWhiteSpace(countryId)) return;

                UpdateProgress(97, "Publishing reconciliation changes to network...");
                LogMessage("Publishing local reconciliation DB to network (one-time import)...");

                await _offlineFirstService.CopyLocalToNetworkAsync(countryId);
                await _offlineFirstService.MarkAllLocalChangesAsSyncedAsync(countryId);

                LogMessage("Publication completed; local ChangeLog entries marked as synced.");

                // Invalidation du cache
                try
                {
                    ReconciliationService.InvalidateReconciliationViewCache(countryId);
                }
                catch { }
            }
            catch (Exception ex)
            {
                LogMessage($"Warning: failed to publish to network after import: {ex.Message}", isError: true);
            }
        }

        #endregion

        #region Logging & Export

        private void LogImportStats(ImportStats stats)
        {
            LogMessage($"ID resolution: Resolved={stats.Resolved:N0}, Unresolved={stats.Unresolved:N0}");
            LogMessage($"Captured fields: Comments={stats.CommentsWithText:N0}, 1ST CLAIM={stats.FirstClaimCount:N0}, LAST CLAIM={stats.LastClaimCount:N0}");
        }

        private void ExportUnresolvedDetailsIfNeeded(ImportStats stats)
        {
            if (stats.UnresolvedDetails.Count == 0) return;

            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecoTool");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var csvPath = Path.Combine(dir, $"import_unresolved_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                var sb = new StringBuilder();
                sb.AppendLine("ExcelID;Comments;FirstClaimDate;LastClaimDate");

                foreach (var it in stats.UnresolvedDetails)
                {
                    sb.Append(CsvEscape(it.ExcelId)).Append(';');
                    sb.Append(CsvEscape(it.Comments)).Append(';');
                    sb.Append(CsvEscape(it.FirstClaim)).Append(';');
                    sb.Append(CsvEscape(it.LastClaim)).AppendLine();
                }

                File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
                LogMessage($"Unresolved details exported to {csvPath}");
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to export unresolved details CSV: {ex.Message}", isError: true);
            }
        }

        private void LogMappingConfiguration(MappingConfig config)
        {
            try
            {
                LogMessage($"Booking: {config.BookingCode}");
                LogMessage($"Pivot start row: {config.PivotStartRow}, Receivable start row: {config.ReceivableStartRow}");
                LogMessage(DumpMapping("Pivot letter→dest", config.PivotLetterToDest));
                LogMessage(DumpMapping("Pivot constants", config.PivotConstants));
                LogMessage(DumpMapping("Receivable letter→dest", config.RecvLetterToDest));
                LogMessage(DumpMapping("Receivable constants", config.RecvConstants));
            }
            catch { }
        }

        private void LogSampleRow(string sheetName, Dictionary<string, object> row)
        {
            string GetValue(string key) => row.TryGetValue(key, out var v) ? v?.ToString() : null;

            LogMessage($"{sheetName} sample: ID={GetValue("ID")}, Comments='{GetValue("Comments")}', " +
                      $"KPI='{GetValue("KPI")}', RiskyItem='{GetValue("RiskyItem")}', " +
                      $"Comments__Color='{GetValue("Comments__Color")}'");
        }

        private string DumpMapping(string title, Dictionary<string, string> map)
        {
            if (map == null || map.Count == 0) return $"{title}: (empty)";
            var items = map.Select(kv => $"{kv.Key}->{kv.Value}");
            return $"{title}: " + string.Join(", ", items);
        }

        private string DumpMapping(string title, Dictionary<string, object> map)
        {
            if (map == null || map.Count == 0) return $"{title}: (empty)";
            var items = map.Select(kv => $"{kv.Key}='{kv.Value}'");
            return $"{title}: " + string.Join(", ", items);
        }

        #endregion

        #region Helper Methods

        private string GetRowValueByPrefix(Dictionary<string, object> row, string prefix)
        {
            foreach (var kv in row)
            {
                var k = kv.Key?.Trim();
                if (string.IsNullOrEmpty(k)) continue;
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return kv.Value?.ToString()?.Trim();
            }
            return null;
        }

        private int GetIntValueByPrefix(Dictionary<string, object> row, string prefix, int fallback)
        {
            var s = GetRowValueByPrefix(row, prefix);
            if (int.TryParse(s, out var n)) return n;
            return fallback;
        }

        private void WarnUnmappedValue(string key, object rawValue, object normalizedValue)
        {
            var rawStr = rawValue?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(rawStr)) return;

            bool looksNumeric = int.TryParse(rawStr, out _);
            bool looksBool = rawStr.Equals("1") || rawStr.Equals("0") ||
                            rawStr.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                            rawStr.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                            rawStr.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                            rawStr.Equals("no", StringComparison.OrdinalIgnoreCase);

            if (Equals(normalizedValue, DBNull.Value) && !(looksNumeric || looksBool))
            {
                LogMessage($"Unmapped label for {key}: '{rawStr}' -> NULL", isError: false);
            }
        }

        private string CsvEscape(string s)
        {
            if (s == null) return string.Empty;
            if (s.Contains(";") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private string NormalizeKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var up = s.Trim().ToUpperInvariant();
            up = up.Replace(" ", string.Empty)
                   .Replace("|", string.Empty)
                   .Replace("\t", string.Empty);
            return up;
        }

        private string FormatDateDdMmYyyy(object v)
        {
            try
            {
                if (TryParseExcelDate(v, out var dt))
                    return dt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            }
            catch { }
            return null;
        }

        private bool TryParseExcelDate(object v, out DateTime result)
        {
            result = default;
            try
            {
                if (v == null) return false;
                if (v is DateTime dt) { result = dt; return true; }
                if (v is double od) { result = DateTime.FromOADate(od); return true; }

                var s = v.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(s)) return false;

                var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
                                     "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy" };

                if (DateTime.TryParseExact(s, formats, CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out result)) return true;
                if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result)) return true;
                if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out result)) return true;
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out result)) return true;
                if (DateTime.TryParse(s, out result)) return true;
            }
            catch { }
            return false;
        }

        private static int? TryGetOleColor(Dictionary<string, object> row, string key)
        {
            if (row == null || key == null) return null;
            if (!row.TryGetValue(key, out var v) || v == null) return null;

            try
            {
                if (v is int i) return i;
                if (v is double d) return (int)Math.Round(d);
                if (int.TryParse(v.ToString(), out var p)) return p;
            }
            catch { }
            return null;
        }

        private static int? InferActionFromColor(int? oleColor)
        {
            if (oleColor == null || oleColor.Value == 0) return null;
            try
            {
                bool isRed = oleColor.Value == 11389944;
                bool isGreen = oleColor.Value == 11854022;
                bool isBlue = oleColor.Value == 15652797;
                bool isYellow = oleColor.Value == 10086143;

                if (isRed) return (int)ActionType.DoPricing;
                if (isYellow) return (int)ActionType.Investigate;
                if (isGreen) return (int)ActionType.Request;
                if (isBlue) return (int)ActionType.NA;
            }
            catch { }
            return null;
        }

        #endregion

        #region Enum Label Mapping

        private static Dictionary<string, int> _actionLabelToId;
        private static Dictionary<string, int> _kpiLabelToId;
        private static Dictionary<string, int> _riskReasonLabelToId;
        private static Dictionary<string, int> _incidentLabelToId;

        private static void EnsureLabelMapsBuilt()
        {
            if (_actionLabelToId != null) return;

            string NormalizeLabel(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                var up = s.Trim().ToUpperInvariant();
                return up.Replace(" ", string.Empty)
                         .Replace("-", string.Empty)
                         .Replace("_", string.Empty)
                         .Replace("/", string.Empty);
            }

            // Build Action mapping
            _actionLabelToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in Enum.GetNames(typeof(ActionType)))
            {
                var value = (int)Enum.Parse(typeof(ActionType), name);
                var key = NormalizeLabel(name);
                if (key != null && !_actionLabelToId.ContainsKey(key))
                    _actionLabelToId[key] = value;

                try
                {
                    var fi = typeof(ActionType).GetField(name);
                    var attrs = (DescriptionAttribute[])fi.GetCustomAttributes(typeof(DescriptionAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                    {
                        var dk = NormalizeLabel(attrs[0].Description);
                        if (dk != null && !_actionLabelToId.ContainsKey(dk))
                            _actionLabelToId[dk] = value;
                    }
                }
                catch { }
            }

            // Build KPI mapping
            _kpiLabelToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in Enum.GetNames(typeof(KPIType)))
            {
                var value = (int)Enum.Parse(typeof(KPIType), name);
                var key = NormalizeLabel(name);
                if (key != null && !_kpiLabelToId.ContainsKey(key))
                    _kpiLabelToId[key] = value;

                try
                {
                    var friendly = EnumHelper.GetKPIName(value);
                    var fk = NormalizeLabel(friendly);
                    if (fk != null && !_kpiLabelToId.ContainsKey(fk))
                        _kpiLabelToId[fk] = value;
                }
                catch { }
            }

            // Build Risky mapping
            _riskReasonLabelToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in Enum.GetNames(typeof(Risky)))
            {
                var value = (int)Enum.Parse(typeof(Risky), name);
                var key = NormalizeLabel(name);
                if (key != null && !_riskReasonLabelToId.ContainsKey(key))
                    _riskReasonLabelToId[key] = value;

                try
                {
                    var fi = typeof(Risky).GetField(name);
                    var attrs = (DescriptionAttribute[])fi.GetCustomAttributes(typeof(DescriptionAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                    {
                        var dk = NormalizeLabel(attrs[0].Description);
                        if (dk != null && !_riskReasonLabelToId.ContainsKey(dk))
                            _riskReasonLabelToId[dk] = value;
                    }
                }
                catch { }
            }

            // Build Incident mapping
            _incidentLabelToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in Enum.GetNames(typeof(INC)))
            {
                var value = (int)Enum.Parse(typeof(INC), name);
                var key = NormalizeLabel(name);
                if (key != null && !_incidentLabelToId.ContainsKey(key))
                    _incidentLabelToId[key] = value;

                try
                {
                    var fi = typeof(INC).GetField(name);
                    var attrs = (DescriptionAttribute[])fi.GetCustomAttributes(typeof(DescriptionAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                    {
                        var dk = NormalizeLabel(attrs[0].Description);
                        if (dk != null && !_incidentLabelToId.ContainsKey(dk))
                            _incidentLabelToId[dk] = value;
                    }
                }
                catch { }
            }
        }

        private static object NormalizeValueForReconciliation(string key, object value)
        {
            if (value == null) return DBNull.Value;

            bool IsDateKey(string k) =>
                k.Equals("FirstClaimDate", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("LastClaimDate", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("ToRemindDate", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("CreationDate", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("DeleteDate", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("LastModified", StringComparison.OrdinalIgnoreCase);

            bool IsBoolKey(string k) =>
                k.Equals("ToRemind", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("ACK", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("RiskyItem", StringComparison.OrdinalIgnoreCase);

            bool IsIntKey(string k) =>
                k.Equals("Action", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("KPI", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("IncidentType", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("ReasonNonRisky", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (IsDateKey(key))
                {
                    if (value is DateTime dt) return dt;
                    if (value is double od) return DateTime.FromOADate(od);

                    var s = value.ToString().Trim();
                    var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
                                         "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy" };

                    if (DateTime.TryParseExact(s, formats, CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out var ex1)) return ex1;
                    if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ex2)) return ex2;
                    if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out var fr)) return fr;
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var inv)) return inv;
                    if (DateTime.TryParse(s, out var parsed)) return parsed;
                    return DBNull.Value;
                }

                if (IsBoolKey(key))
                {
                    if (value is bool b) return b;
                    var s = value.ToString().Trim();
                    if (string.Equals(s, "1") || s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(s, "0") || s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
                    return DBNull.Value;
                }

                if (IsIntKey(key))
                {
                    if (value is int i) return i;
                    if (value is double d) return (int)Math.Round(d);
                    var s = value.ToString().Trim();
                    if (int.TryParse(s, out var p)) return p;

                    // Tentative de mapping texte -> ID via enums
                    EnsureLabelMapsBuilt();
                    string NormalizeLabel(string t)
                    {
                        if (string.IsNullOrWhiteSpace(t)) return null;
                        var up = t.Trim().ToUpperInvariant();
                        return up.Replace(" ", string.Empty)
                                 .Replace("-", string.Empty)
                                 .Replace("_", string.Empty)
                                 .Replace("/", string.Empty);
                    }

                    var nk = NormalizeLabel(s);
                    if (nk != null)
                    {
                        if (key.Equals("Action", StringComparison.OrdinalIgnoreCase) &&
                            _actionLabelToId.TryGetValue(nk, out var aid)) return aid;
                        if (key.Equals("KPI", StringComparison.OrdinalIgnoreCase) &&
                            _kpiLabelToId.TryGetValue(nk, out var kid)) return kid;
                        if (key.Equals("ReasonNonRisky", StringComparison.OrdinalIgnoreCase) &&
                            _riskReasonLabelToId.TryGetValue(nk, out var rid)) return rid;
                        if (key.Equals("IncidentType", StringComparison.OrdinalIgnoreCase) &&
                            _incidentLabelToId.TryGetValue(nk, out var iid)) return iid;
                    }

                    return DBNull.Value;
                }

                // Par défaut: string ou pass-through
                if (value is string) return value;
                return value;
            }
            catch
            {
                return DBNull.Value;
            }
        }

        #endregion

        #region UI Update Methods

        private void UpdateProgress(int percentage, string status)
        {
            Dispatcher.Invoke(() =>
            {
                ImportProgressBar.Value = percentage;
                ProgressStatusText.Text = status;
            });
        }

        private void LogMessage(string message, bool isError = false)
        {
            Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                var prefix = isError ? "[ERROR]" : "[INFO]";
                ImportLogText.Text += $"{timestamp} {prefix} {message}\n";
                LogScrollViewer.ScrollToEnd();
            });
        }

        private void ShowWarning(string message)
        {
            MessageBox.Show(message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ShowError(string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}