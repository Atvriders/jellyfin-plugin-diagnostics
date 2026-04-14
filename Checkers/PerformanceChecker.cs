using System.Diagnostics;
using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class PerformanceChecker : IDiagnosticChecker
{
    private readonly IApplicationPaths _appPaths;
    private readonly IServerConfigurationManager _serverConfigManager;
    private readonly LogAnalyzer _logAnalyzer;
    private readonly ILogger<PerformanceChecker> _logger;

    public string Name => "Performance";
    public string Category => "Performance";

    public PerformanceChecker(
        IApplicationPaths appPaths,
        IServerConfigurationManager serverConfigManager,
        LogAnalyzer logAnalyzer,
        ILogger<PerformanceChecker> logger)
    {
        _appPaths = appPaths;
        _serverConfigManager = serverConfigManager;
        _logAnalyzer = logAnalyzer;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        CheckDiskIoLatency(results);
        CheckFreeMemory(results);
        CheckLoadAverage(results);
        CheckCacheBloat(results);
        CheckTranscodeTempSpace(results);
        CheckSlowRequestLogs(results);

        return Task.FromResult(results);
    }

    private void CheckDiskIoLatency(List<DiagnosticResult> results)
    {
        var configPath = _appPaths.ConfigurationDirectoryPath;
        if (!Directory.Exists(configPath))
        {
            return;
        }

        var testFile = Path.Combine(configPath, ".diag_io_test_" + Guid.NewGuid().ToString("N"));
        var buffer = new byte[1024 * 1024]; // 1 MB
        new Random().NextBytes(buffer);

        try
        {
            var sw = Stopwatch.StartNew();
            using (var fs = new FileStream(testFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (int i = 0; i < 10; i++)
                {
                    fs.Write(buffer, 0, buffer.Length);
                }
                fs.Flush(true);
            }
            sw.Stop();
            var writeMs = sw.ElapsedMilliseconds;

            sw.Restart();
            using (var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var readBuf = new byte[1024 * 1024];
                while (fs.Read(readBuf, 0, readBuf.Length) > 0) { }
            }
            sw.Stop();
            var readMs = sw.ElapsedMilliseconds;

            var mbsWrite = 10000.0 / Math.Max(writeMs, 1);
            var mbsRead = 10000.0 / Math.Max(readMs, 1);

            var severity = DiagnosticSeverity.Info;
            var status = DiagnosticStatus.Working;

            if (mbsWrite < 10 || mbsRead < 20)
            {
                severity = DiagnosticSeverity.Critical;
                status = DiagnosticStatus.Broken;
            }
            else if (mbsWrite < 50 || mbsRead < 100)
            {
                severity = DiagnosticSeverity.Warning;
                status = DiagnosticStatus.Degraded;
            }

            results.Add(new DiagnosticResult
            {
                Severity = severity,
                Status = status,
                Category = Category,
                Title = "Config disk I/O: " + mbsWrite.ToString("F0") + " MB/s write, " + mbsRead.ToString("F0") + " MB/s read",
                Detail = "10 MB sequential write+fsync took " + writeMs + " ms; read took " + readMs + " ms.",
                UnraidContext = severity != DiagnosticSeverity.Info
                    ? "Slow I/O on /config is the single biggest cause of perceived Jellyfin slowness. On Unraid this almost always means /config is on the array (spinning disks) instead of the cache pool (SSD)."
                    : string.Empty,
                FixSteps = severity != DiagnosticSeverity.Info
                    ? new List<string>
                    {
                        "Stop the Jellyfin container",
                        "Copy appdata to the cache pool: cp -a /mnt/user/appdata/jellyfin /mnt/cache/appdata/jellyfin",
                        "Update the container's /config mapping to /mnt/cache/appdata/jellyfin",
                        "Set the 'appdata' share to 'Use cache pool: Prefer' or 'Only'",
                        "Restart and re-run diagnostics"
                    }
                    : new List<string>()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disk I/O test failed");
        }
        finally
        {
            try
            {
                if (File.Exists(testFile))
                {
                    File.Delete(testFile);
                }
            }
            catch { }
        }
    }

    private void CheckFreeMemory(List<DiagnosticResult> results)
    {
        try
        {
            if (!File.Exists("/proc/meminfo"))
            {
                return;
            }

            var lines = File.ReadAllLines("/proc/meminfo");
            long totalKb = 0, availKb = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    totalKb = ParseKb(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    availKb = ParseKb(line);
                }
            }

            if (totalKb == 0)
            {
                return;
            }

            var availMb = availKb / 1024;
            var totalMb = totalKb / 1024;
            var pctFree = availKb * 100.0 / totalKb;

            DiagnosticSeverity sev;
            DiagnosticStatus stat;

            if (availMb < 512 || pctFree < 5)
            {
                sev = DiagnosticSeverity.Critical;
                stat = DiagnosticStatus.Broken;
            }
            else if (availMb < 1024 || pctFree < 15)
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
                Title = "Free memory: " + availMb + " MB of " + totalMb + " MB (" + pctFree.ToString("F0") + "%)",
                Detail = "Available memory inside the Jellyfin container.",
                UnraidContext = sev != DiagnosticSeverity.Info
                    ? "Low memory forces Jellyfin to evict its in-memory caches and slows down metadata loading. If this is a Docker container with a memory limit, the limit may be too low."
                    : string.Empty,
                FixSteps = sev != DiagnosticSeverity.Info
                    ? new List<string>
                    {
                        "Unraid Docker settings: edit the Jellyfin container",
                        "Increase the memory limit (or remove it) under Extra Parameters",
                        "A reasonable limit for a busy server is 4-8 GB; remove the limit entirely if you have spare RAM",
                        "Also check that your Unraid host has free RAM (Main tab > Memory)"
                    }
                    : new List<string>()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory check failed");
        }
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
        {
            return kb;
        }
        return 0;
    }

    private void CheckLoadAverage(List<DiagnosticResult> results)
    {
        try
        {
            if (!File.Exists("/proc/loadavg"))
            {
                return;
            }
            var content = File.ReadAllText("/proc/loadavg").Trim();
            var parts = content.Split(' ');
            if (parts.Length < 3)
            {
                return;
            }

            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var load1)
                || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var load5))
            {
                return;
            }

            var cores = Environment.ProcessorCount;
            var loadRatio = load5 / Math.Max(cores, 1);

            DiagnosticSeverity sev = DiagnosticSeverity.Info;
            DiagnosticStatus stat = DiagnosticStatus.Working;

            if (loadRatio > 2.0)
            {
                sev = DiagnosticSeverity.Critical;
                stat = DiagnosticStatus.Broken;
            }
            else if (loadRatio > 1.0)
            {
                sev = DiagnosticSeverity.Warning;
                stat = DiagnosticStatus.Degraded;
            }

            results.Add(new DiagnosticResult
            {
                Severity = sev,
                Status = stat,
                Category = Category,
                Title = "Load average: " + load1.ToString("F2") + " (1m), " + load5.ToString("F2") + " (5m) on " + cores + " cores",
                Detail = "5-minute load ratio vs core count: " + loadRatio.ToString("F2"),
                UnraidContext = sev != DiagnosticSeverity.Info
                    ? "Load average > core count indicates CPU is saturated. On Unraid this is often caused by concurrent software transcodes. Enable hardware acceleration or limit simultaneous streams."
                    : string.Empty,
                FixSteps = sev != DiagnosticSeverity.Info
                    ? new List<string>
                    {
                        "Dashboard > Playback > Transcoding > enable hardware acceleration (VAAPI / QSV / NVENC)",
                        "Dashboard > Playback > set max simultaneous transcodes",
                        "Check the Dashboard Activity view for concurrent streams",
                        "Consider upgrading CPU or adding a GPU for transcoding"
                    }
                    : new List<string>()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load average check failed");
        }
    }

    private void CheckCacheBloat(List<DiagnosticResult> results)
    {
        try
        {
            var cachePath = _appPaths.CachePath;
            if (!Directory.Exists(cachePath))
            {
                return;
            }

            long totalBytes = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                    catch { }
                }
            }
            catch { }

            var sizeGb = totalBytes / (1024.0 * 1024 * 1024);

            DiagnosticSeverity sev = DiagnosticSeverity.Info;
            DiagnosticStatus stat = DiagnosticStatus.Working;

            if (sizeGb > 20)
            {
                sev = DiagnosticSeverity.Warning;
                stat = DiagnosticStatus.Degraded;
            }

            results.Add(new DiagnosticResult
            {
                Severity = sev,
                Status = stat,
                Category = Category,
                Title = "Cache directory size: " + sizeGb.ToString("F1") + " GB",
                Detail = "Total size of the Jellyfin cache directory (" + cachePath + ").",
                UnraidContext = sev != DiagnosticSeverity.Info
                    ? "A bloated cache won't break Jellyfin but wastes space on the Unraid cache pool."
                    : string.Empty,
                FixSteps = sev != DiagnosticSeverity.Info
                    ? new List<string>
                    {
                        "Stop Jellyfin",
                        "Clear the cache directory contents (safe; it will be rebuilt)",
                        "Start Jellyfin"
                    }
                    : new List<string>()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache bloat check failed");
        }
    }

    private void CheckTranscodeTempSpace(List<DiagnosticResult> results)
    {
        try
        {
            var encodingOptions = _serverConfigManager.GetEncodingOptions();
            var transcodePath = string.IsNullOrEmpty(encodingOptions.TranscodingTempPath)
                ? Path.Combine(_appPaths.CachePath, "transcodes")
                : encodingOptions.TranscodingTempPath;

            if (!Directory.Exists(transcodePath))
            {
                // Not a failure; Jellyfin creates it on first use
                return;
            }

            var drive = new DriveInfo(Path.GetPathRoot(transcodePath) ?? "/");
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);

            DiagnosticSeverity sev = DiagnosticSeverity.Info;
            DiagnosticStatus stat = DiagnosticStatus.Working;

            if (freeGb < 2)
            {
                sev = DiagnosticSeverity.Critical;
                stat = DiagnosticStatus.Broken;
            }
            else if (freeGb < 10)
            {
                sev = DiagnosticSeverity.Warning;
                stat = DiagnosticStatus.Degraded;
            }

            results.Add(new DiagnosticResult
            {
                Severity = sev,
                Status = stat,
                Category = Category,
                Title = "Transcode temp free space: " + freeGb.ToString("F1") + " GB",
                Detail = "Free space on the filesystem containing the transcode temp directory (" + transcodePath + ").",
                UnraidContext = sev != DiagnosticSeverity.Info
                    ? "Transcoding 4K content can consume tens of GB per active stream. Running out of space mid-stream kills playback. On Unraid, point the transcode path at an SSD cache or RAM disk."
                    : string.Empty,
                FixSteps = sev != DiagnosticSeverity.Info
                    ? new List<string>
                    {
                        "Dashboard > Playback > Transcoding > Transcoding temp path",
                        "Point it at a large SSD (/mnt/cache/transcodes) or a tmpfs mount",
                        "For tmpfs: add --mount type=tmpfs,destination=/transcodes,tmpfs-size=16G to the container Extra Parameters",
                        "Free space on the current transcode filesystem"
                    }
                    : new List<string>()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcode temp check failed");
        }
    }

    private void CheckSlowRequestLogs(List<DiagnosticResult> results)
    {
        var patterns = new Dictionary<string, string>
        {
            { "slow_request", @"(slow request|took \d{4,} ms|elapsed \d{4,})" },
            { "request_timeout", @"(request.*timeout|timed out waiting)" },
            { "task_canceled", @"(TaskCanceledException|OperationCanceledException).*http" }
        };

        var logResults = _logAnalyzer.ScanLogs(patterns);
        int total = logResults.Values.Sum(v => v.Count);
        if (total == 0)
        {
            return;
        }

        results.Add(new DiagnosticResult
        {
            Severity = total > 20 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
            Status = total > 20 ? DiagnosticStatus.Degraded : DiagnosticStatus.Working,
            Category = Category,
            Title = "Slow or timed-out requests in logs (" + total + ")",
            Detail = "Recent log lines matched slow-request or timeout patterns.",
            UnraidContext = "Slow HTTP responses from Jellyfin on Unraid typically trace back to (1) slow /config storage, (2) concurrent database writes during a library scan, or (3) overloaded transcodes starving the UI.",
            FixSteps = new List<string>
            {
                "Check the database health report in this same diagnostic",
                "Verify /config is on the Unraid cache pool",
                "Check Dashboard > Scheduled Tasks for a running library scan during the slow period",
                "Reduce simultaneous transcodes under Playback settings"
            }
        });
    }
}
