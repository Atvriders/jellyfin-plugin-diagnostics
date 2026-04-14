using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class PermissionsChecker : IDiagnosticChecker
{
    private readonly IApplicationPaths _appPaths;
    private readonly LogAnalyzer _logAnalyzer;
    private readonly ILogger<PermissionsChecker> _logger;

    public string Name => "Permissions & Filesystem";
    public string Category => "Permissions";

    public PermissionsChecker(
        IApplicationPaths appPaths,
        LogAnalyzer logAnalyzer,
        ILogger<PermissionsChecker> logger)
    {
        _appPaths = appPaths;
        _logAnalyzer = logAnalyzer;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        CheckDirectoryWritable(results, "Config", _appPaths.ConfigurationDirectoryPath);
        CheckDirectoryWritable(results, "Cache", _appPaths.CachePath);
        CheckDirectoryWritable(results, "Data", _appPaths.DataPath);
        CheckDirectoryWritable(results, "Log", _appPaths.LogDirectoryPath);

        var metadataPath = Path.Combine(_appPaths.DataPath, "metadata");
        if (Directory.Exists(metadataPath))
        {
            CheckDirectoryWritable(results, "Metadata", metadataPath);
        }

        CheckPermissionLogs(results);

        return Task.FromResult(results);
    }

    private void CheckDirectoryWritable(List<DiagnosticResult> results, string dirName, string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = dirName + " directory does not exist",
                Detail = "The " + dirName.ToLowerInvariant() + " directory '" + dirPath + "' does not exist.",
                UnraidContext = "This directory should be created automatically by Jellyfin. If it's missing, the volume mapping for /config may be incorrect.",
                FixSteps = new List<string>
                {
                    "Check the Jellyfin container's volume mapping for /config",
                    "Ensure the host path exists and is writable",
                    "Restart the container to let Jellyfin recreate its directories"
                }
            });
            return;
        }

        var testFile = Path.Combine(dirPath, ".diag_write_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(testFile, string.Empty);
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = dirName + " directory is writable",
                Detail = "The " + dirName.ToLowerInvariant() + " directory '" + dirPath + "' is writable.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }
        catch (UnauthorizedAccessException)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = dirName + " directory is NOT writable",
                Detail = "Jellyfin cannot write to '" + dirPath + "'. This will cause database errors and prevent proper operation.",
                UnraidContext = "On Unraid, this usually means the container's PUID/PGID doesn't match the file ownership, or the host directory permissions are too restrictive.",
                FixSteps = new List<string>
                {
                    "On the Unraid console, check ownership of the host config path",
                    "Fix ownership: chown -R 99:100 <host_config_path>",
                    "Fix permissions: chmod -R 755 <host_config_path>",
                    "Verify PUID=99 and PGID=100 are set in the container environment",
                    "Restart the container"
                }
            });
        }
        catch (Exception ex)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Unknown,
                Category = Category,
                Title = "Could not verify " + dirName + " writability",
                Detail = "An error occurred testing write access to '" + dirPath + "': " + ex.Message,
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
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
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private void CheckPermissionLogs(List<DiagnosticResult> results)
    {
        var patterns = new Dictionary<string, string>
        {
            { "permission_denied", @"(permission denied|access.*denied|unauthorized access)" },
            { "sqlite_readonly", @"(SQLITE_READONLY|database is locked|unable to open database)" },
            { "sqlite_error", @"(SqliteException|Microsoft\.Data\.Sqlite)" },
            { "io_error", @"(IOException|System\.IO\.IOException).*(permission|access|denied)" }
        };

        var logResults = _logAnalyzer.ScanLogs(patterns);

        int permErrors = logResults["permission_denied"].Count;
        int sqliteErrors = logResults["sqlite_readonly"].Count + logResults["sqlite_error"].Count;

        if (permErrors > 0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = permErrors > 5 ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Degraded,
                Category = Category,
                Title = "Permission denied errors in logs (" + permErrors + ")",
                Detail = "Found " + permErrors + " permission-denied error(s) in recent Jellyfin logs.",
                UnraidContext = "Permission errors in Docker containers on Unraid almost always indicate a UID/GID mismatch between the container process and the files it's trying to access.",
                FixSteps = new List<string>
                {
                    "Check PUID and PGID environment variables in the container settings",
                    "Set PUID=99 and PGID=100 to match Unraid defaults",
                    "Fix ownership on the host: chown -R 99:100 /mnt/user/appdata/jellyfin",
                    "Restart the container"
                }
            });
        }

        if (sqliteErrors > 0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "SQLite errors in logs (" + sqliteErrors + ")",
                Detail = "Found " + sqliteErrors + " SQLite error(s) in recent logs. This can cause database corruption and Jellyfin instability.",
                UnraidContext = "SQLite write failures on Unraid Docker are commonly caused by: (1) the config directory being on a network share instead of local storage, (2) permission issues on the appdata path, or (3) the container running out of disk space.",
                FixSteps = new List<string>
                {
                    "Ensure the Jellyfin config/data path is on local storage (e.g., /mnt/user/appdata/), not a remote share",
                    "Check available disk space on the Unraid cache drive",
                    "Fix permissions: chown -R 99:100 /mnt/user/appdata/jellyfin",
                    "If the database is corrupted, stop Jellyfin and check the .db files in the data directory",
                    "Restart the container after fixing"
                }
            });
        }

        if (permErrors == 0 && sqliteErrors == 0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = "No permission or SQLite errors in logs",
                Detail = "No permission-denied or SQLite write errors were found in recent log entries.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }
    }
}
