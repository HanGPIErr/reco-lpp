namespace RecoTool.Services.DTOs
{
    /// <summary>
    /// DTO for status counts used in TodoCard display
    /// </summary>
    public class StatusCountsDto
    {
        public int NewCount { get; set; }
        public int UpdatedCount { get; set; }
        public int NotLinkedCount { get; set; }
        public int NotGroupedCount { get; set; }
        public int DiscrepancyCount { get; set; }
        public int BalancedCount { get; set; }
    }
}
