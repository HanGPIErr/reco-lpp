using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoTool.Services
{
    /// <summary>
    /// Résultat d'un import Ambre
    /// </summary>
    public class ImportResult
    {
        public string CountryId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool IsSuccess { get; set; }
        public int ProcessedRecords { get; set; }
        public int NewRecords { get; set; }
        public int UpdatedRecords { get; set; }
        public int DeletedRecords { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> ValidationErrors { get; set; } = new List<string>();

        /// <summary>
        /// Total number of Excel rows that were silently dropped during the
        /// country-account filtering step (account didn't match the country's
        /// Pivot or Receivable). Useful to surface "you imported 1000 rows but
        /// 50 had foreign accounts and were ignored".
        /// </summary>
        public int SkippedRows { get; set; }

        /// <summary>
        /// Up to ~20 sample skip reasons (e.g. "Account 12345/Entity 99
        /// doesn't match FR Pivot/Receivable"). Capped to keep the UI summary
        /// readable; the count above gives the full picture.
        /// </summary>
        public List<string> SkippedRowSamples { get; set; } = new List<string>();

        public TimeSpan Duration => EndTime - StartTime;
        public bool HasErrors => Errors.Any() || ValidationErrors.Any();
    }
}
