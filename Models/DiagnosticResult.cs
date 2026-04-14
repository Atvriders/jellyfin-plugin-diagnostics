namespace JellyfinDiagnostics.Models;

public class DiagnosticResult
{
    public DiagnosticSeverity Severity { get; set; }
    public DiagnosticStatus Status { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string UnraidContext { get; set; } = string.Empty;
    public List<string> FixSteps { get; set; } = new();
}
