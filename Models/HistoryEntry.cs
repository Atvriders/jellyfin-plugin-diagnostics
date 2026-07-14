namespace JellyfinDiagnostics.Models;

public class HistoryEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public ButtonAction Action { get; set; }
    public string UserName { get; set; } = string.Empty;
    public HistoryOutcome Outcome { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public int? CriticalCount { get; set; }
    public int? WarningCount { get; set; }
    public int? InfoCount { get; set; }
    public string? JellyfinVersion { get; set; }
    public string? OperatingSystem { get; set; }
    public bool HasReport { get; set; }
}
