using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfflineFirstAccess.Helpers;
using OfflineFirstAccess.Models;
using RecoTool.Helpers;
using RecoTool.Models;
using RecoTool.Services.Helpers;

namespace RecoTool.Services.Ambre
{
    /// <summary>
    /// Validateur pour l'import Ambre
    /// </summary>
    public class AmbreImportValidator
    {
        /// <summary>
        /// Valide les fichiers d'import
        /// </summary>
        public string[] ValidateFiles(string[] filePaths, bool isMultiFile, ImportResult result)
        {
            var files = (filePaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Take(2)
                .ToArray();

            if (files.Length == 0)
            {
                result.Errors.Add("No files provided");
                return Array.Empty<string>();
            }

            foreach (var filePath in files)
            {
                ValidateSingleFile(filePath, isMultiFile, result);
            }

            return files;
        }

        /// <summary>
        /// Valide que les comptes requis sont présents dans les données
        /// </summary>
        public bool ValidateRequiredAccounts(
            List<Dictionary<string, object>> filteredData,
            Country country,
            ImportResult result)
        {
            var accounts = ExtractUniqueAccounts(filteredData);
            
            bool hasPivot = ValidateAccount(accounts, country?.CNT_AmbrePivot, "Pivot");
            bool hasReceivable = ValidateAccount(accounts, country?.CNT_AmbreReceivable, "Receivable");

            if (!(hasPivot && hasReceivable))
            {
                var missing = new List<string>();
                if (!hasPivot) missing.Add($"Pivot={country?.CNT_AmbrePivot}");
                if (!hasReceivable) missing.Add($"Receivable={country?.CNT_AmbreReceivable}");
                
                result.Errors.Add($"Import aborted: both AMBRE accounts are required. Missing: {string.Join(", ", missing)}.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validates each row's coherence against the country configuration. Per-row issues are
        /// reported as <b>non-blocking</b> warnings on <paramref name="result"/> when provided —
        /// every input row is still returned in <c>validData</c> so the import behavior is unchanged
        /// from the previous no-op contract (legacy callers / tests pass <c>result = null</c> and
        /// observe an empty <c>errors</c> list and full <c>validData</c>).
        /// </summary>
        /// <remarks>
        /// Why non-blocking: the original method was an intentional NO-OP, so historical imports
        /// have been silently passing rows that fail <see cref="ValidationHelper.ValidateDataCoherence"/>
        /// (foreign accounts, zero amounts, etc.). Activating drop-on-error would suddenly fail
        /// real-world imports. We surface visibility first; if you later want to enforce, switch
        /// the conditional below to skip <c>validData.Add(row)</c> on failure.
        /// </remarks>
        public (List<string> errors, List<DataAmbre> validData) ValidateTransformedData(
            List<DataAmbre> data,
            Country country,
            ImportResult result = null)
        {
            var errors = new List<string>();
            var validData = new List<DataAmbre>(data?.Count ?? 0);
            if (data == null) return (errors, validData);

            int warningCount = 0;
            List<string> samples = null;

            foreach (var row in data)
            {
                validData.Add(row);
                // Skip the cost of per-row coherence checks when no result sink is provided
                // (preserves the pre-existing no-op contract under the tests).
                if (result == null) continue;

                var rowErrors = ValidationHelper.ValidateDataCoherence(row, country);
                if (rowErrors == null || rowErrors.Count == 0) continue;
                foreach (var msg in rowErrors)
                {
                    warningCount++;
                    if (samples == null) samples = new List<string>(8);
                    if (samples.Count < 5) samples.Add($"{row.GetUniqueKey()}: {msg}");
                }
            }

            if (warningCount > 0 && result != null)
            {
                var sample = (samples != null && samples.Count > 0)
                    ? " | sample: " + string.Join(" / ", samples)
                    : string.Empty;
                result.Warnings.Add($"{warningCount} data coherence issue(s) detected across {validData.Count} row(s) — import proceeded.{sample}");
            }
            return (errors, validData);
        }

        private void ValidateSingleFile(string filePath, bool isMultiFile, ImportResult result)
        {
            var errors = ValidationHelper.ValidateImportFile(filePath);
            
            if (errors.Any())
            {
                if (isMultiFile)
                {
                    var fileName = Path.GetFileName(filePath);
                    result.Errors.AddRange(errors.Select(e => $"{fileName}: {e}"));
                }
                else
                {
                    result.Errors.AddRange(errors);
                }
            }
        }

        private List<string> ExtractUniqueAccounts(List<Dictionary<string, object>> data)
        {
            return data
                .Select(r => r.ContainsKey("Account_ID") ? r["Account_ID"]?.ToString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool ValidateAccount(List<string> accounts, string expectedAccount, string accountType)
        {
            if (string.IsNullOrWhiteSpace(expectedAccount))
                return false;

            return accounts.Any(a => string.Equals(a, expectedAccount, StringComparison.OrdinalIgnoreCase));
        }
    }
}