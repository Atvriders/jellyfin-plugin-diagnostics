using System.Runtime.InteropServices;
using JellyfinDiagnostics.Checkers;
using JellyfinDiagnostics.Models;
using MediaBrowser.Common;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Services;

public class DiagnosticsService
{
    private readonly IEnumerable<IDiagnosticChecker> _checkers;
    private readonly IApplicationHost _appHost;
    private readonly ILogger<DiagnosticsService> _logger;

    private DiagnosticsReport? _lastReport;

    public DiagnosticsService(
        IEnumerable<IDiagnosticChecker> checkers,
        IApplicationHost appHost,
        ILogger<DiagnosticsService> logger)
    {
        _checkers = checkers;
        _appHost = appHost;
        _logger = logger;
    }

    public async Task<DiagnosticsReport> RunAllAsync(CancellationToken cancellationToken)
    {
        var report = new DiagnosticsReport
        {
            Timestamp = DateTime.UtcNow,
            JellyfinVersion = _appHost.ApplicationVersionString,
            OperatingSystem = RuntimeInformation.OSDescription
        };

        foreach (var checker in _checkers)
        {
            try
            {
                _logger.LogInformation("Running diagnostic checker: {CheckerName}", checker.Name);
                var results = await checker.RunAsync(cancellationToken).ConfigureAwait(false);
                report.Results.AddRange(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Diagnostic checker '{CheckerName}' failed", checker.Name);
                report.Results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Unknown,
                    Category = checker.Category,
                    Title = "Checker failed: " + checker.Name,
                    Detail = "The " + checker.Name + " checker threw an exception: " + ex.Message,
                    UnraidContext = "This is an internal plugin error. The checker could not complete its analysis.",
                    FixSteps = new List<string>
                    {
                        "Check the Jellyfin log for more details about this error",
                        "If this persists, report it as a plugin bug"
                    }
                });
            }
        }

        _lastReport = report;
        return report;
    }

    public DiagnosticsReport? GetLastReport() => _lastReport;
}
