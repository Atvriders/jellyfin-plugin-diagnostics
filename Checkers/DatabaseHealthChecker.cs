using System.Data.Common;
using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class DatabaseHealthChecker : IDiagnosticChecker
{
    private readonly IApplicationPaths _appPaths;
    private readonly LogAnalyzer _logAnalyzer;
    private readonly ILogger<DatabaseHealthChecker> _logger;

    public string Name => "Database Health";
    public string Category => "Database";

    private const long WarnSizeBytes = 1L * 1024 * 1024 * 1024;   // 1 GB
    private const long CritSizeBytes = 5L * 1024 * 1024 * 1024;   // 5 GB
    private const long WarnWalBytes = 64L * 1024 * 1024;          // 64 MB
    private const long CritWalBytes = 512L * 1024 * 1024;         // 512 MB

    public DatabaseHealthChecker(
        IApplicationPaths appPaths,
        LogAnalyzer logAnalyzer,
        ILogger<DatabaseHealthChecker> logger)
    {
        _appPaths = appPaths;
        _logAnalyzer = logAnalyzer;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        var dataPath = _appPaths.DataPath;
        var dbFiles = new[] { "library.db", "jellyfin.db", "playback_reporting.db" };

        foreach (var name in dbFiles)
        {
            var path = Path.Combine(dataPath, name);
            if (!File.Exists(path))
            {
                continue;
            }

            CheckDatabaseSize(results, name, path);
            CheckWalFile(results, name, path);
            CheckDatabaseIntegrity(results, name, path, cancellationToken);
            CheckJournalMode(results, name, path);
        }

        CheckDatabaseOnNetworkShare(results, dataPath);
        CheckDatabaseLogErrors(results);

        return Task.FromResult(results);
    }

    private void CheckDatabaseSize(List<DiagnosticResult> results, string name, string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            var sizeMb = size / (1024.0 * 1024.0);

            if (size > CritSizeBytes)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = name + " is very large (" + sizeMb.ToString("F0") + " MB)",
                    Detail = name + " has grown beyond 5 GB. SQLite performance degrades and backups become slow.",
                    UnraidContext = "On Unraid, a massive library.db can indicate either a very large library or database bloat from failed cleanup. Consider running Dashboard > Scheduled Tasks > Clean Database.",
                    FixSteps = new List<string>
                    {
                        "Dashboard > Scheduled Tasks > run 'Clean Database' and 'Optimize Database'",
                        "Back up the database (copy " + name + " while Jellyfin is stopped)",
                        "If growth is unexpected, check for duplicate library scans creating redundant entries",
                        "Consider moving the database to faster storage (Unraid cache drive) if on array"
                    }
                });
            }
            else if (size > WarnSizeBytes)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = name + " is large (" + sizeMb.ToString("F0") + " MB)",
                    Detail = name + " is over 1 GB. This is normal for a large library but worth monitoring.",
                    UnraidContext = "Large databases on slow storage (Unraid spinning array) are a common source of perceived Jellyfin slowness. Ensure the database lives on the Unraid cache pool.",
                    FixSteps = new List<string>
                    {
                        "Verify /config (or wherever the DB lives) is mapped to the Unraid cache pool, not the array",
                        "Schedule regular 'Optimize Database' runs",
                        "Back up before any manual cleanup"
                    }
                });
            }
            else
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Info,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = name + " size: " + sizeMb.ToString("F1") + " MB",
                    Detail = "Database file size is within normal range.",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stat {Path}", path);
        }
    }

    private void CheckWalFile(List<DiagnosticResult> results, string name, string dbPath)
    {
        var walPath = dbPath + "-wal";
        if (!File.Exists(walPath))
        {
            return;
        }

        try
        {
            var size = new FileInfo(walPath).Length;
            var sizeMb = size / (1024.0 * 1024.0);

            if (size > CritWalBytes)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Broken,
                    Category = Category,
                    Title = name + "-wal is huge (" + sizeMb.ToString("F0") + " MB)",
                    Detail = "The SQLite write-ahead log for " + name + " has grown past 512 MB. This means checkpoints are not running, usually due to a long-held read transaction or filesystem issues.",
                    UnraidContext = "A large WAL on Unraid often indicates the database is on a filesystem that doesn't support proper fsync (network share, FUSE mount). The database MUST be on a local Unraid cache drive for SQLite WAL to work correctly.",
                    FixSteps = new List<string>
                    {
                        "Stop Jellyfin",
                        "Verify the /config volume mapping points to the Unraid cache pool (e.g., /mnt/cache/appdata/jellyfin)",
                        "Never map /config to /mnt/user/... on an NFS or SMB remote share",
                        "After moving, delete the -wal and -shm files alongside the .db",
                        "Start Jellyfin and verify the WAL stays small"
                    }
                });
            }
            else if (size > WarnWalBytes)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = name + "-wal is large (" + sizeMb.ToString("F0") + " MB)",
                    Detail = "SQLite write-ahead log is larger than expected. Under normal operation it should checkpoint and stay small.",
                    UnraidContext = "Possible causes: long-running library scan holding a read lock, background task stuck, or slow underlying storage.",
                    FixSteps = new List<string>
                    {
                        "Check Dashboard > Scheduled Tasks for stuck or long-running jobs",
                        "Run 'Optimize Database' to force a checkpoint",
                        "Monitor WAL size over time"
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stat WAL file {Path}", walPath);
        }
    }

    private void CheckDatabaseIntegrity(List<DiagnosticResult> results, string name, string dbPath, CancellationToken cancellationToken)
    {
        try
        {
            var connString = "Data Source=" + dbPath + ";Mode=ReadOnly;Cache=Shared";
            using var conn = new SqliteConnection(connString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            using var reader = cmd.ExecuteReader();

            var issues = new List<string>();
            while (reader.Read())
            {
                var row = reader.GetString(0);
                if (!row.Equals("ok", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(row);
                }
            }

            if (issues.Count == 0)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Info,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = name + " integrity check: OK",
                    Detail = "SQLite PRAGMA quick_check passed for " + name + ".",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
            }
            else
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Broken,
                    Category = Category,
                    Title = name + " integrity check FAILED",
                    Detail = "SQLite quick_check returned " + issues.Count + " issue(s). Database corruption is likely. First issue: " + issues[0],
                    UnraidContext = "Database corruption on Unraid Docker is almost always caused by: (1) storing the DB on a network share, (2) hard shutdowns/power loss while writes are pending, (3) running out of disk space on the cache drive.",
                    FixSteps = new List<string>
                    {
                        "Stop Jellyfin immediately",
                        "Back up the current (corrupt) " + name + " file",
                        "If you have a recent backup, restore from it",
                        "Otherwise, use the SQLite CLI to recover: sqlite3 " + name + " '.recover' | sqlite3 recovered.db",
                        "Replace the corrupt file with the recovered one",
                        "Verify /config is on local Unraid cache pool storage, not a network share"
                    }
                });
            }
        }
        catch (DbException ex)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Unknown,
                Category = Category,
                Title = name + " integrity check could not run",
                Detail = "Failed to open database for integrity check: " + ex.Message,
                UnraidContext = "If this happens while Jellyfin is running, SQLite may be holding an exclusive lock. Ideally this check runs against a live DB, but some lock modes prevent it.",
                FixSteps = new List<string>
                {
                    "Retry the scan later",
                    "If persistent, inspect the database manually with sqlite3 " + Path.GetFileName(dbPath)
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Integrity check failed for {Name}", name);
        }
    }

    private void CheckJournalMode(List<DiagnosticResult> results, string name, string dbPath)
    {
        try
        {
            var connString = "Data Source=" + dbPath + ";Mode=ReadOnly;Cache=Shared";
            using var conn = new SqliteConnection(connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var mode = cmd.ExecuteScalar() as string ?? "unknown";

            if (!mode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = name + " journal mode is " + mode + " (not WAL)",
                    Detail = "Jellyfin expects SQLite WAL journal mode for concurrent read/write. " + mode + " may cause write contention.",
                    UnraidContext = "Jellyfin sets WAL mode at startup. If it's not WAL, the filesystem may not support it (e.g., the DB is on a network share).",
                    FixSteps = new List<string>
                    {
                        "Verify the database resides on a local filesystem (Unraid cache pool)",
                        "Restart Jellyfin so it re-enables WAL",
                        "If WAL still doesn't stick, the underlying storage does not support it"
                    }
                });
            }
        }
        catch
        {
            // Best-effort
        }
    }

    private void CheckDatabaseOnNetworkShare(List<DiagnosticResult> results, string dataPath)
    {
        try
        {
            if (!File.Exists("/proc/mounts"))
            {
                return;
            }

            var mounts = File.ReadAllLines("/proc/mounts");
            string? matchedMount = null;
            string? matchedFs = null;

            foreach (var line in mounts)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    continue;
                }
                var mountPoint = parts[1];
                if (dataPath.StartsWith(mountPoint, StringComparison.Ordinal)
                    && (matchedMount == null || mountPoint.Length > matchedMount.Length))
                {
                    matchedMount = mountPoint;
                    matchedFs = parts[2];
                }
            }

            if (matchedFs == null)
            {
                return;
            }

            var networkFilesystems = new[] { "nfs", "nfs4", "cifs", "smbfs", "smb3", "fuse.sshfs", "fuse.rclone" };

            if (Array.Exists(networkFilesystems, fs => fs.Equals(matchedFs, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Broken,
                    Category = Category,
                    Title = "Database is on a network share (" + matchedFs + ")",
                    Detail = "The Jellyfin data directory '" + dataPath + "' is mounted as " + matchedFs + ". SQLite corruption is very likely.",
                    UnraidContext = "Never put the Jellyfin /config or data directory on an NFS/SMB share. Use the local Unraid cache pool (e.g., /mnt/cache/appdata/jellyfin) or a dedicated SSD pool.",
                    FixSteps = new List<string>
                    {
                        "Stop the Jellyfin container",
                        "Copy /mnt/user/appdata/jellyfin to /mnt/cache/appdata/jellyfin",
                        "Update the container volume mapping for /config to the cache path",
                        "Set the share to 'Use cache pool: Only' so mover doesn't migrate it back to the array",
                        "Restart the container"
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check database mount");
        }
    }

    private void CheckDatabaseLogErrors(List<DiagnosticResult> results)
    {
        var patterns = new Dictionary<string, string>
        {
            { "db_corrupt", @"(database disk image is malformed|corrupt)" },
            { "db_locked", @"(database is locked|SQLITE_BUSY)" },
            { "db_io_error", @"(SQLITE_IOERR|disk I/O error)" },
            { "db_fullfs", @"(database or disk is full|SQLITE_FULL)" }
        };

        var logResults = _logAnalyzer.ScanLogs(patterns);
        int total = logResults.Values.Sum(v => v.Count);
        if (total == 0)
        {
            return;
        }

        var breakdown = string.Join(", ",
            logResults.Where(kv => kv.Value.Count > 0)
                      .Select(kv => kv.Key + "=" + kv.Value.Count));

        results.Add(new DiagnosticResult
        {
            Severity = logResults["db_corrupt"].Count > 0 || logResults["db_fullfs"].Count > 0
                ? DiagnosticSeverity.Critical
                : DiagnosticSeverity.Warning,
            Status = DiagnosticStatus.Degraded,
            Category = Category,
            Title = "Database errors in logs (" + total + " total)",
            Detail = "Database-related error patterns found in recent logs: " + breakdown,
            UnraidContext = "Frequent database errors on Unraid usually mean the DB is on slow or remote storage. Move it to the cache pool.",
            FixSteps = new List<string>
            {
                "Check full Jellyfin logs for context around each error",
                "Verify /config volume is on local cache storage",
                "Run 'Optimize Database' in Scheduled Tasks",
                "If corruption messages appear, back up and restore from a known-good snapshot"
            }
        });
    }
}
