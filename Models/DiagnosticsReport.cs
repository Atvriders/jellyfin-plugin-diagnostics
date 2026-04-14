namespace JellyfinDiagnostics.Models;

public class DiagnosticsReport
{
    public DateTime Timestamp { get; set; }
    public string JellyfinVersion { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public List<DiagnosticResult> Results { get; set; } = new();
}
