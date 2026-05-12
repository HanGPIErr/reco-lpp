using System;
using System.Threading.Tasks;
using RecoTool.Models;

namespace RecoTool.Services
{
    /// <summary>
    /// Abstraction over <see cref="AmbreImportService"/> so that
    /// <see cref="RecoTool.ViewModels.ImportAmbreViewModel"/> and other consumers
    /// can be mocked in unit tests without instantiating the full import pipeline.
    /// </summary>
    public interface IAmbreImportService
    {
        /// <summary>
        /// Imports one or multiple Ambre Excel files (merged) for a given country.
        /// Up to two file paths are honoured by the underlying pipeline.
        /// </summary>
        Task<ImportResult> ImportAmbreFiles(
            string[] filePaths,
            string countryId,
            Action<string, int> progressCallback = null);

        /// <summary>Imports a single Ambre Excel file for a given country.</summary>
        Task<ImportResult> ImportAmbreFile(
            string filePath,
            string countryId,
            Action<string, int> progressCallback = null);
    }
}
