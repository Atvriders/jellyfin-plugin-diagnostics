using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class ContainerResourceChecker : IDiagnosticChecker
{
    private readonly LogAnalyzer _logAnalyzer;
    private readonly ILogger<ContainerResourceChecker> _logger;

    public string Name => "Container Resources";
    public string Category => "Container Resources";

    public ContainerResourceChecker(LogAnalyzer logAnalyzer, ILogger<ContainerResourceChecker> logger)
    {
        _logAnalyzer = logAnalyzer;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        CheckCgroupMemoryLimit(results);
        CheckCgroupCpuLimit(results);
        CheckSwapUsage(results);
        CheckOomLog(results);

        return Task.FromResult(results);
    }

    private void CheckCgroupMemoryLimit(List<DiagnosticResult> results)
    {
        try
        {
            // cgroup v2
            var maxPath = "/sys/fs/cgroup/memory.max";
            var currentPath = "/sys/fs/cgroup/memory.current";
            long? maxBytes = null;
            long? currentBytes = null;

            if (File.Exists(maxPath))
            {
                var content = File.ReadAllText(maxPath).Trim();
                if (content != "max" && long.TryParse(content, out var v))
                {
                    maxBytes = v;
                }
            }

            if (File.Exists(currentPath))
            {
                var content = File.ReadAllText(currentPath).Trim();
                if (long.TryParse(content, out var c))
                {
                    currentBytes = c;
                }
            }

            // cgroup v1 fallback
            if (maxBytes == null && File.Exists("/sys/fs/cgroup/memory/memory.limit_in_bytes"))
            {
                var content = File.ReadAllText("/sys/fs/cgroup/memory/memory.limit_in_bytes").Trim();
                if (long.TryParse(content, out var v) && v < long.MaxValue / 2)
                {
                    maxBytes = v;
                }
            }
            if (currentBytes == null && File.Exists("/sys/fs/cgroup/memory/memory.usage_in_bytes"))
            {
                var content = File.ReadAllText("/sys/fs/cgroup/memory/memory.usage_in_bytes").Trim();
                if (long.TryParse(content, out var c))
                {
                    currentBytes = c;
                }
            }

            if (maxBytes == null || maxBytes == 0)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Info,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = "No container memory limit",
                    Detail = "The container has no enforced memory limit (or the limit is unreachable).",
                    UnraidContext = "This is the common default on Unraid when the Docker template does not set a memory limit. Fine for most users.",
                    FixSteps = new List<string>()
                });
                return;
            }

            var maxMb = maxBytes.Value / (1024.0 * 1024);
            var maxGb = maxMb / 1024;

            if (currentBytes.HasValue)
            {
                var currentMb = currentBytes.Value / (1024.0 * 1024);
                var pct = currentBytes.Value * 100.0 / maxBytes.Value;

                DiagnosticSeverity sev;
                DiagnosticStatus stat;

                if (pct > 95)
                {
                    sev = DiagnosticSeverity.Critical;
                    stat = DiagnosticStatus.Broken;
                }
                else if (pct > 80)
                {
                    sev = DiagnosticSeverity.Warning;
                    stat = DiagnosticStatus.Degraded;
                }
                else
                {
                    sev = DiagnosticSeverity.Info;
                    stat = DiagnosticStatus.Working;
                }

                results.Add(new DiagnosticResult
                {
                    Severity = sev,
                    Status = stat,
                    Category = Category,
                    Title = "Container memory: " + currentMb.ToString("F0") + " MB / " + maxMb.ToString("F0") + " MB (" + pct.ToString("F0") + "%)",
                    Detail = "Current cgroup memory usage vs limit.",
                    UnraidContext = sev != DiagnosticSeverity.Info
                        ? "The container is running near its memory limit. Jellyfin will be OOM-killed when it crosses 100%. Either raise the limit or remove it."
                        : string.Empty,
                    FixSteps = sev != DiagnosticSeverity.Info
                        ? new List<string>
                        {
                            "Unraid Docker tab > edit Jellyfin container",
                            "Increase or remove the memory limit (Extra Parameters --memory=8g or remove entirely)",
                            "Rule of thumb: 2 GB baseline + 1 GB per concurrent transcode",
                            "Apply changes and restart the container"
                        }
                        : new List<string>()
                });
            }
            else
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Info,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = "Container memory limit: " + maxGb.ToString("F1") + " GB",
                    Detail = "Cgroup memory limit enforced on the Jellyfin container.",
                    UnraidContext = maxGb < 2
                        ? "Less than 2 GB is tight for Jellyfin. Consider raising to at least 4 GB."
                        : string.Empty,
                    FixSteps = new List<string>()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory cgroup check failed");
        }
    }

    private void CheckCgroupCpuLimit(List<DiagnosticResult> results)
    {
        try
        {
            // cgroup v2: cpu.max is "<quota> <period>" or "max <period>"
            var path = "/sys/fs/cgroup/cpu.max";
            if (!File.Exists(path))
            {
                return;
            }
            var content = File.ReadAllText(path).Trim();
            var parts = content.Split(' ');
            if (parts.Length != 2)
            {
                return;
            }

            if (parts[0] == "max")
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Info,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = "No container CPU limit",
                    Detail = "Container may use all available host CPU.",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
                return;
            }

            if (!long.TryParse(parts[0], out var quota) || !long.TryParse(parts[1], out var period) || period == 0)
            {
                return;
            }

            var effectiveCores = quota / (double)period;

            results.Add(new DiagnosticResult
            {
                Severity = effectiveCores < 2 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                Status = effectiveCores < 2 ? DiagnosticStatus.Degraded : DiagnosticStatus.Working,
                Category = Category,
                Title = "Container CPU quota: " + effectiveCores.ToString("F1") + " cores",
                Detail = "cgroup cpu.max quota=" + quota + " period=" + period,
                UnraidContext = effectiveCores < 2
                    ? "Jellyfin needs at least 2 cores for responsive UI, and more for software transcoding. A tight CPU limit causes UI lag and slow scans."
                    : string.Empty,
                FixSteps = effectiveCores < 2
                    ? new List<string>
                    {
                        "Unraid Docker tab > edit Jellyfin container",
                        "Remove or raise --cpus / --cpu-quota from Extra Parameters",
                        "Apply and restart"
                    }
                    : new List<string>()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CPU cgroup check failed");
        }
    }

    private void CheckSwapUsage(List<DiagnosticResult> results)
    {
        try
        {
            if (!File.Exists("/proc/meminfo"))
            {
                return;
            }
            var lines = File.ReadAllLines("/proc/meminfo");
            long swapTotal = 0;
            long swapFree = 0;
            foreach (var l in lines)
            {
                if (l.StartsWith("SwapTotal:", StringComparison.Ordinal)) swapTotal = ParseKb(l);
                else if (l.StartsWith("SwapFree:", StringComparison.Ordinal)) swapFree = ParseKb(l);
            }
            if (swapTotal == 0) return;
            var used = swapTotal - swapFree;
            var usedMb = used / 1024;
            if (usedMb > 256)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = "Container is using " + usedMb + " MB of swap",
                    Detail = "SwapFree=" + (swapFree / 1024) + " MB, SwapTotal=" + (swapTotal / 1024) + " MB.",
                    UnraidContext = "Jellyfin relies on SQLite which hates swap. Latency spikes when the database pages get swapped out. Raise container memory instead of relying on swap.",
                    FixSteps = new List<string>
                    {
                        "Raise the Jellyfin container memory limit (or remove it)",
                        "On Unraid, consider disabling swap for Docker entirely if host RAM is sufficient",
                        "Check host memory usage in Unraid Main > Memory"
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Swap check failed");
        }
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb)) return kb;
        return 0;
    }

    private void CheckOomLog(List<DiagnosticResult> results)
    {
        var patterns = new Dictionary<string, string>
        {
            { "oom", @"(OutOfMemoryException|Out of memory|oom-kill|killed process)" }
        };
        var logResults = _logAnalyzer.ScanLogs(patterns);
        if (logResults["oom"].Count == 0)
        {
            return;
        }

        results.Add(new DiagnosticResult
        {
            Severity = DiagnosticSeverity.Critical,
            Status = DiagnosticStatus.Broken,
            Category = Category,
            Title = "Out-of-memory events in logs (" + logResults["oom"].Count + ")",
            Detail = "OOM-related log entries were found. Jellyfin was likely killed by the kernel.",
            UnraidContext = "OOM kills on Unraid Docker mean either the container memory limit is too low, the host ran out of RAM, or a memory leak. Raising the limit is usually the fix.",
            FixSteps = new List<string>
            {
                "Raise or remove the container memory limit in Unraid",
                "Check Unraid host memory usage in the Main tab",
                "If the host itself is tight, reduce other containers or add RAM"
            }
        });
    }
}
