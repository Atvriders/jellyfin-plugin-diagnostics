using System.Runtime.InteropServices;
using JellyfinDiagnostics.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class VolumePathChecker : IDiagnosticChecker
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<VolumePathChecker> _logger;

    public string Name => "Volume & Path Mapping";
    public string Category => "Docker Volumes";

    public VolumePathChecker(
        ILibraryManager libraryManager,
        ILogger<VolumePathChecker> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();
        CheckLibraryPaths(results);
        CheckMountPoints(results);
        CheckUidGid(results);
        return Task.FromResult(results);
    }

    private void CheckLibraryPaths(List<DiagnosticResult> results)
    {
        var virtualFolders = _libraryManager.GetVirtualFolders();

        if (virtualFolders.Count == 0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Unknown,
                Category = Category,
                Title = "No libraries configured",
                Detail = "No media libraries are configured in Jellyfin.",
                UnraidContext = "Add libraries in Jellyfin Dashboard > Libraries, pointing to paths that are mapped into the Docker container.",
                FixSteps = new List<string>
                {
                    "Go to Jellyfin Dashboard > Libraries",
                    "Click 'Add Media Library'",
                    "Select the media type and browse to the mapped path inside the container"
                }
            });
            return;
        }

        foreach (var folder in virtualFolders)
        {
            if (folder.Locations == null || folder.Locations.Length == 0)
            {
                continue;
            }

            foreach (var path in folder.Locations)
            {
                CheckSinglePath(results, folder.Name, path);
            }
        }
    }

    private void CheckSinglePath(List<DiagnosticResult> results, string libraryName, string path)
    {
        if (!Directory.Exists(path))
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "Library path does not exist: " + libraryName,
                Detail = "The path '" + path + "' for library '" + libraryName + "' does not exist inside the container.",
                UnraidContext = "This usually means the volume mapping is missing or incorrect in the Unraid Docker container settings. The path inside the container must match what Jellyfin is configured to use.",
                FixSteps = new List<string>
                {
                    "In Unraid, edit the Jellyfin Docker container",
                    "Add or fix the volume mapping: Host path (e.g., /mnt/user/media) -> Container path (" + path + ")",
                    "Ensure the host path exists on Unraid",
                    "Apply changes and restart the container"
                }
            });
            return;
        }

        try
        {
            var entries = Directory.GetFileSystemEntries(path);
            if (entries.Length == 0)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = "Library path is empty: " + libraryName,
                    Detail = "The path '" + path + "' exists but contains no files or directories.",
                    UnraidContext = "This could mean the Unraid share hasn't been populated yet, or the volume mapping points to the wrong directory.",
                    FixSteps = new List<string>
                    {
                        "Verify the host path on Unraid contains media files",
                        "On the Unraid console, run: ls -la <host_path_for_" + libraryName + ">",
                        "Check that the volume mapping maps the correct host directory"
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
                    Title = "Library path OK: " + libraryName,
                    Detail = "The path '" + path + "' exists and is accessible (" + entries.Length + " entries found).",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "Library path not readable: " + libraryName,
                Detail = "The path '" + path + "' exists but Jellyfin cannot read it (permission denied).",
                UnraidContext = "On Unraid, this typically happens when the container runs as a different UID/GID than the file owner. Unraid commonly uses UID 99 / GID 100 (nobody/users).",
                FixSteps = new List<string>
                {
                    "Check the Jellyfin container's PUID and PGID environment variables",
                    "Set them to match the Unraid default: PUID=99, PGID=100",
                    "Or on the Unraid console, fix permissions: chown -R 99:100 <host_path>",
                    "Restart the container after making changes"
                }
            });
        }
    }

    private void CheckMountPoints(List<DiagnosticResult> results)
    {
        try
        {
            if (!File.Exists("/proc/mounts"))
            {
                return;
            }

            var mounts = File.ReadAllLines("/proc/mounts");
            foreach (var mount in mounts)
            {
                var parts = mount.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                {
                    continue;
                }

                var mountPoint = parts[1];
                var options = parts[3];

                if (!mountPoint.StartsWith("/media", StringComparison.OrdinalIgnoreCase)
                    && !mountPoint.StartsWith("/data", StringComparison.OrdinalIgnoreCase)
                    && !mountPoint.StartsWith("/mnt", StringComparison.OrdinalIgnoreCase)
                    && !mountPoint.StartsWith("/movies", StringComparison.OrdinalIgnoreCase)
                    && !mountPoint.StartsWith("/tv", StringComparison.OrdinalIgnoreCase)
                    && !mountPoint.StartsWith("/music", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (options.Contains("ro,") || options.StartsWith("ro,") || options == "ro")
                {
                    results.Add(new DiagnosticResult
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Status = DiagnosticStatus.Degraded,
                        Category = Category,
                        Title = "Read-only mount detected: " + mountPoint,
                        Detail = "The mount point '" + mountPoint + "' is mounted as read-only. Jellyfin won't be able to write metadata or subtitles to this location.",
                        UnraidContext = "In Unraid Docker settings, volume mappings can be set to read-only (RO) or read-write (RW). Media paths are usually fine as RO, but if Jellyfin needs to save .nfo files or subtitles alongside media, RW is required.",
                        FixSteps = new List<string>
                        {
                            "In Unraid, edit the Jellyfin Docker container",
                            "Find the volume mapping for " + mountPoint,
                            "Change it from Read Only to Read/Write if needed",
                            "Apply changes and restart the container"
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse /proc/mounts");
        }
    }

    private void CheckUidGid(List<DiagnosticResult> results)
    {
        try
        {
            int processUid = -1;
            int processGid = -1;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (File.Exists("/proc/self/status"))
                {
                    var statusLines = File.ReadAllLines("/proc/self/status");
                    foreach (var line in statusLines)
                    {
                        if (line.StartsWith("Uid:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1 && int.TryParse(parts[1], out var uid))
                            {
                                processUid = uid;
                            }
                        }
                        else if (line.StartsWith("Gid:", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 1 && int.TryParse(parts[1], out var gid))
                            {
                                processGid = gid;
                            }
                        }
                    }
                }
            }

            if (processUid < 0 || processGid < 0)
            {
                return;
            }

            if (processUid != 99 || processGid != 100)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = "Non-standard UID/GID: " + processUid + ":" + processGid,
                    Detail = "Jellyfin is running as UID " + processUid + " / GID " + processGid + ". The typical Unraid default is 99:100 (nobody:users).",
                    UnraidContext = "Running with a non-standard UID/GID on Unraid can cause permission issues with media files and shares, which default to 99:100 ownership.",
                    FixSteps = new List<string>
                    {
                        "In Unraid, edit the Jellyfin Docker container",
                        "Set environment variable PUID=99",
                        "Set environment variable PGID=100",
                        "Apply changes and restart the container",
                        "Note: If you intentionally use different IDs, ensure your media files have matching ownership"
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
                    Title = "UID/GID matches Unraid defaults (99:100)",
                    Detail = "Jellyfin is running with the standard Unraid UID/GID, which should have proper access to Unraid shares.",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check UID/GID");
        }
    }
}
