using JellyfinDiagnostics.Models;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class ScheduledTasksChecker : IDiagnosticChecker
{
    private readonly ITaskManager _taskManager;
    private readonly ILogger<ScheduledTasksChecker> _logger;

    public string Name => "Scheduled Tasks";
    public string Category => "Scheduled Tasks";

    public ScheduledTasksChecker(ITaskManager taskManager, ILogger<ScheduledTasksChecker> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        List<IScheduledTaskWorker> tasks;
        try
        {
            tasks = _taskManager.ScheduledTasks?.ToList() ?? new List<IScheduledTaskWorker>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate scheduled tasks");
            return Task.FromResult(results);
        }

        if (tasks.Count == 0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Unknown,
                Category = Category,
                Title = "No scheduled tasks visible",
                Detail = "The plugin could not enumerate any scheduled tasks via ITaskManager.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
            return Task.FromResult(results);
        }

        int failedCount = 0;
        int longRunningCount = 0;
        int neverRunCount = 0;
        var problemTasks = new List<string>();

        foreach (var task in tasks)
        {
            try
            {
                var lastResult = task.LastExecutionResult;
                if (lastResult == null)
                {
                    neverRunCount++;
                    continue;
                }

                if (lastResult.Status == TaskCompletionStatus.Failed)
                {
                    failedCount++;
                    problemTasks.Add(task.Name + " (failed: " + (lastResult.ErrorMessage ?? "no error message") + ")");
                }

                var duration = lastResult.EndTimeUtc - lastResult.StartTimeUtc;
                if (duration > TimeSpan.FromHours(1))
                {
                    longRunningCount++;
                    problemTasks.Add(task.Name + " (ran for " + duration.TotalMinutes.ToString("F0") + " min)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect task {TaskName}", task.Name);
            }
        }

        if (failedCount > 0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = failedCount + " scheduled task(s) failed on last run",
                Detail = "Failed tasks:\n" + string.Join("\n", problemTasks.Where(s => s.Contains("failed"))),
                UnraidContext = "Failed tasks often indicate permission problems on library paths, network share issues, or Jellyfin service errors.",
                FixSteps = new List<string>
                {
                    "Dashboard > Scheduled Tasks > click the failed task > view error details",
                    "Common causes: library path not accessible, database locked, out of disk space",
                    "After fixing the underlying issue, manually trigger the task again"
                }
            });
        }

        if (longRunningCount > 0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Degraded,
                Category = Category,
                Title = longRunningCount + " task(s) took over an hour to complete",
                Detail = "Long-running tasks:\n" + string.Join("\n", problemTasks.Where(s => s.Contains("min"))),
                UnraidContext = "Very long task runs on Unraid are usually a symptom of slow /config storage or large libraries being scanned from the array instead of the cache.",
                FixSteps = new List<string>
                {
                    "Verify /config lives on the Unraid cache pool",
                    "For library scans, check that the scan is not re-indexing unchanged files",
                    "Consider splitting very large libraries",
                    "Check disk I/O latency in the Performance section of this report"
                }
            });
        }

        if (neverRunCount > 0 && neverRunCount < tasks.Count)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = neverRunCount + " task(s) have never run",
                Detail = "Some scheduled tasks have no recorded last execution.",
                UnraidContext = "Normal if Jellyfin was recently installed or some tasks are manually triggered only.",
                FixSteps = new List<string>()
            });
        }

        if (failedCount == 0 && longRunningCount == 0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = "Scheduled tasks healthy (" + tasks.Count + " tracked)",
                Detail = "No failed or long-running tasks detected.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }

        return Task.FromResult(results);
    }
}
