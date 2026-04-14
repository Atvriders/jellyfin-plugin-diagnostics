using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Services;

public class LogAnalyzer
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<LogAnalyzer> _logger;

    public LogAnalyzer(IApplicationPaths appPaths, ILogger<LogAnalyzer> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    public Dictionary<string, List<string>> ScanLogs(
        Dictionary<string, string> patterns,
        int maxLines = 5000)
    {
        var results = new Dictionary<string, List<string>>();
        foreach (var key in patterns.Keys)
        {
            results[key] = new List<string>();
        }

        var logDir = _appPaths.LogDirectoryPath;
        if (!Directory.Exists(logDir))
        {
            _logger.LogWarning("Log directory not found: {LogDir}", logDir);
            return results;
        }

        var logFiles = Directory.GetFiles(logDir, "log_*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (logFiles.Count == 0)
        {
            logFiles = Directory.GetFiles(logDir, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }

        var compiledPatterns = patterns.ToDictionary(
            kvp => kvp.Key,
            kvp => new Regex(kvp.Value, RegexOptions.IgnoreCase | RegexOptions.Compiled));

        int linesRead = 0;

        foreach (var logFile in logFiles)
        {
            if (linesRead >= maxLines)
            {
                break;
            }

            try
            {
                var lines = File.ReadLines(logFile);
                foreach (var line in lines)
                {
                    if (linesRead >= maxLines)
                    {
                        break;
                    }

                    linesRead++;

                    foreach (var (patternName, regex) in compiledPatterns)
                    {
                        if (regex.IsMatch(line))
                        {
                            results[patternName].Add(line.Trim());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read log file: {File}", logFile);
            }
        }

        return results;
    }
}
