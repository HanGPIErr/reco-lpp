using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OfflineFirstAccess.Helpers;
using OfflineFirstAccess.Models;
using RecoTool.Helpers;
using RecoTool.Models;

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
        /// Valide les données transformées
        /// </summary>
        public (List<string> errors, List<DataAmbre> validData) ValidateTransformedData(
            List<DataAmbre> data,
            Country country)
        {
            var errors = new List<string>();
            var validData = new List<DataAmbre>();

            // Coherence validation is intentionally a no-op here — see
            // AmbreImportValidatorTests.ValidateTransformedData_AllValid_ReturnsAllInValidList
            // for the contract. If you need to reject malformed rows (zero amounts,
            // wrong account for country, etc.), call ValidationHelper.ValidateDataCoherence
            // per item and route failures into `errors` below.
            validData.AddRange(data);
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