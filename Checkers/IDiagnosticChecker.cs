using JellyfinDiagnostics.Models;

namespace JellyfinDiagnostics.Checkers;

public interface IDiagnosticChecker
{
    string Name { get; }
    string Category { get; }
    Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken);
}
