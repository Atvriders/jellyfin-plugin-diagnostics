# Jellyfin AI Diagnostics Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Jellyfin server-side admin plugin that detects common Docker/Unraid configuration issues and presents findings in a dashboard UI.

**Architecture:** Modular checker pattern — each detection area (HW accel, volumes, permissions, networking) is an independent `IDiagnosticChecker`. A `DiagnosticsService` orchestrates them. REST API serves results to an embedded HTML admin dashboard. Optional AI integration sends sanitized reports to an external endpoint.

**Tech Stack:** C# / .NET 8.0 / Jellyfin Plugin API 10.11.x / Vanilla HTML+CSS+JS for admin UI

---

## File Structure

```
jellyfin-plugin-diagnostics/
├── JellyfinDiagnostics.csproj
├── Plugin.cs
├── PluginConfiguration.cs
├── Models/
│   ├── DiagnosticSeverity.cs
│   ├── DiagnosticStatus.cs
│   ├── DiagnosticResult.cs
│   └── DiagnosticsReport.cs
├── Checkers/
│   ├── IDiagnosticChecker.cs
│   ├── HardwareAccelerationChecker.cs
│   ├── VolumePathChecker.cs
│   ├── PermissionsChecker.cs
│   └── NetworkChecker.cs
├── Services/
│   ├── DiagnosticsService.cs
│   ├── LogAnalyzer.cs
│   └── AiIntegrationService.cs
├── Api/
│   └── DiagnosticsController.cs
├── Pages/
│   └── diagnosticsPage.html
├── build.yaml
└── meta.json
```

---

### Task 1: Project Scaffolding — .csproj, meta.json, build.yaml

**Files:**
- Create: `JellyfinDiagnostics.csproj`
- Create: `meta.json`
- Create: `build.yaml`

- [ ] **Step 1: Create JellyfinDiagnostics.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>JellyfinDiagnostics</RootNamespace>
    <AssemblyName>JellyfinDiagnostics</AssemblyName>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="10.11.*" />
    <PackageReference Include="Jellyfin.Model" Version="10.11.*" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="Pages/diagnosticsPage.html" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create meta.json**

```json
{
  "guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "name": "Jellyfin Diagnostics",
  "description": "Admin diagnostics plugin for detecting common Docker/Unraid configuration issues",
  "overview": "Analyzes Jellyfin server configuration and environment to detect hardware acceleration, volume mapping, permission, and networking issues.",
  "owner": "Atvriders",
  "category": "General",
  "versions": [
    {
      "version": "1.0.0.0",
      "changelog": "Initial release",
      "targetAbi": "10.11.0.0",
      "sourceUrl": "",
      "timestamp": "2026-04-14T00:00:00Z"
    }
  ]
}
```

- [ ] **Step 3: Create build.yaml**

```yaml
name: "Jellyfin Diagnostics Plugin"
guid: "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
version: "1.0.0.0"
targetAbi: "10.11.0.0"
framework: "net8.0"
overview: "Admin diagnostics for Docker/Unraid Jellyfin deployments"
artifacts:
  - "JellyfinDiagnostics.dll"
```

- [ ] **Step 4: Commit**

```bash
git add JellyfinDiagnostics.csproj meta.json build.yaml
git commit -m "feat: scaffold project with csproj, meta.json, build.yaml"
```

---

### Task 2: Models — Enums and Data Classes

**Files:**
- Create: `Models/DiagnosticSeverity.cs`
- Create: `Models/DiagnosticStatus.cs`
- Create: `Models/DiagnosticResult.cs`
- Create: `Models/DiagnosticsReport.cs`

- [ ] **Step 1: Create DiagnosticSeverity.cs**

```csharp
namespace JellyfinDiagnostics.Models;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Critical
}
```

- [ ] **Step 2: Create DiagnosticStatus.cs**

```csharp
namespace JellyfinDiagnostics.Models;

public enum DiagnosticStatus
{
    Working,
    Degraded,
    Broken,
    Unknown
}
```

- [ ] **Step 3: Create DiagnosticResult.cs**

```csharp
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
```

- [ ] **Step 4: Create DiagnosticsReport.cs**

```csharp
namespace JellyfinDiagnostics.Models;

public class DiagnosticsReport
{
    public DateTime Timestamp { get; set; }
    public string JellyfinVersion { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public List<DiagnosticResult> Results { get; set; } = new();
}
```

- [ ] **Step 5: Commit**

```bash
git add Models/
git commit -m "feat: add diagnostic models (severity, status, result, report)"
```

---

### Task 3: Checker Interface

**Files:**
- Create: `Checkers/IDiagnosticChecker.cs`

- [ ] **Step 1: Create IDiagnosticChecker.cs**

```csharp
using JellyfinDiagnostics.Models;

namespace JellyfinDiagnostics.Checkers;

public interface IDiagnosticChecker
{
    string Name { get; }
    string Category { get; }
    Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Commit**

```bash
git add Checkers/IDiagnosticChecker.cs
git commit -m "feat: add IDiagnosticChecker interface"
```

---

### Task 4: LogAnalyzer Service

**Files:**
- Create: `Services/LogAnalyzer.cs`

- [ ] **Step 1: Create LogAnalyzer.cs**

```csharp
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
```

- [ ] **Step 2: Commit**

```bash
git add Services/LogAnalyzer.cs
git commit -m "feat: add LogAnalyzer service for scanning Jellyfin logs"
```

---

### Task 5: HardwareAccelerationChecker

**Files:**
- Create: `Checkers/HardwareAccelerationChecker.cs`

- [ ] **Step 1: Create HardwareAccelerationChecker.cs**

This is the largest checker. It detects: HW accel type from config, NVIDIA device nodes + nvidia-smi, VAAPI/QSV /dev/dri nodes, FFmpeg encoder availability, and transcode log errors.

Full code in the spec — create at `Checkers/HardwareAccelerationChecker.cs` with these methods:
- `RunAsync` — orchestrator: checks config, delegates to sub-checks
- `CheckNvidiaDevices` — probes /dev/nvidia*, runs nvidia-smi
- `CheckDriDevices` — probes /dev/dri, checks for renderD* nodes
- `CheckFfmpegEncoders` — runs `ffmpeg -encoders`, checks for h264_nvenc/vaapi/qsv
- `CheckTranscodeLogs` — uses LogAnalyzer with transcode error patterns

```csharp
using System.Diagnostics;
using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class HardwareAccelerationChecker : IDiagnosticChecker
{
    private readonly IServerConfigurationManager _configManager;
    private readonly LogAnalyzer _logAnalyzer;
    private readonly ILogger<HardwareAccelerationChecker> _logger;

    public string Name => "Hardware Acceleration";
    public string Category => "Hardware Acceleration";

    public HardwareAccelerationChecker(
        IServerConfigurationManager configManager,
        LogAnalyzer logAnalyzer,
        ILogger<HardwareAccelerationChecker> logger)
    {
        _configManager = configManager;
        _logAnalyzer = logAnalyzer;
        _logger = logger;
    }

    public async Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        var encodingOptions = _configManager.GetEncodingOptions();
        var hwAccelType = encodingOptions.HardwareAccelerationType ?? string.Empty;

        if (string.IsNullOrEmpty(hwAccelType) || hwAccelType.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = "Hardware acceleration is disabled",
                Detail = "No hardware acceleration is configured. Transcoding will use CPU only.",
                UnraidContext = "This is fine if you don't need transcoding or prefer CPU encoding. To enable GPU transcoding on Unraid, you need to pass through a GPU device to the Docker container.",
                FixSteps = new List<string>
                {
                    "In Unraid Docker settings, edit the Jellyfin container",
                    "Add a device mapping for your GPU (e.g., /dev/dri for Intel/AMD, or NVIDIA runtime)",
                    "In Jellyfin Dashboard > Playback > Transcoding, select your acceleration type",
                    "Save and restart the container"
                }
            });
            return results;
        }

        results.Add(new DiagnosticResult
        {
            Severity = DiagnosticSeverity.Info,
            Status = DiagnosticStatus.Working,
            Category = Category,
            Title = "Hardware acceleration type: " + hwAccelType,
            Detail = "Jellyfin is configured to use " + hwAccelType + " for hardware-accelerated transcoding.",
            UnraidContext = "Verify the corresponding GPU device is passed through to the Docker container in Unraid.",
            FixSteps = new List<string>()
        });

        if (hwAccelType.Equals("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            await CheckNvidiaDevices(results, cancellationToken).ConfigureAwait(false);
        }
        else if (hwAccelType.Equals("vaapi", StringComparison.OrdinalIgnoreCase)
                 || hwAccelType.Equals("qsv", StringComparison.OrdinalIgnoreCase))
        {
            CheckDriDevices(results, hwAccelType);
        }

        await CheckFfmpegEncoders(results, hwAccelType, cancellationToken).ConfigureAwait(false);
        CheckTranscodeLogs(results);

        return results;
    }

    private async Task CheckNvidiaDevices(List<DiagnosticResult> results, CancellationToken cancellationToken)
    {
        bool hasNvidiaDevices = false;
        try
        {
            if (Directory.Exists("/dev"))
            {
                var nvidiaDevices = Directory.GetFiles("/dev", "nvidia*");
                hasNvidiaDevices = nvidiaDevices.Length > 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check /dev/nvidia* devices");
        }

        if (!hasNvidiaDevices)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "NVIDIA device nodes not found",
                Detail = "NVENC is configured but no /dev/nvidia* devices are visible inside the container.",
                UnraidContext = "On Unraid, NVIDIA devices must be passed through to the Docker container. This requires the Nvidia-Driver plugin installed on Unraid and the container configured with --runtime=nvidia or explicit device mappings.",
                FixSteps = new List<string>
                {
                    "Install the 'Nvidia-Driver' plugin from Unraid Community Applications",
                    "In Unraid Docker settings, edit the Jellyfin container",
                    "Add '--runtime=nvidia' to Extra Parameters",
                    "Add environment variable NVIDIA_VISIBLE_DEVICES=all",
                    "Add environment variable NVIDIA_DRIVER_CAPABILITIES=all",
                    "Apply changes and restart the container"
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
                Title = "NVIDIA device nodes found",
                Detail = "NVIDIA GPU devices are visible inside the container at /dev/nvidia*.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }

        bool nvidiaSmiAvailable = false;
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            nvidiaSmiAvailable = process.ExitCode == 0;
        }
        catch
        {
            nvidiaSmiAvailable = false;
        }

        if (!nvidiaSmiAvailable)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Degraded,
                Category = Category,
                Title = "nvidia-smi not available",
                Detail = "The nvidia-smi command is not available inside the container. GPU status cannot be verified, but transcoding may still work if device nodes are present.",
                UnraidContext = "nvidia-smi is typically included when the NVIDIA runtime is properly configured. Its absence may indicate an incomplete NVIDIA setup.",
                FixSteps = new List<string>
                {
                    "Ensure '--runtime=nvidia' is set in the container's Extra Parameters",
                    "Verify the Nvidia-Driver plugin is installed and up to date on Unraid",
                    "Restart the container after making changes"
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
                Title = "nvidia-smi is available",
                Detail = "NVIDIA GPU driver tools are accessible inside the container.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }
    }

    private void CheckDriDevices(List<DiagnosticResult> results, string hwAccelType)
    {
        bool hasRenderNode = false;
        try
        {
            if (Directory.Exists("/dev/dri"))
            {
                var driFiles = Directory.GetFiles("/dev/dri");
                hasRenderNode = driFiles.Any(f => Path.GetFileName(f).StartsWith("renderD", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check /dev/dri devices");
        }

        if (!Directory.Exists("/dev/dri"))
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "/dev/dri not found",
                Detail = hwAccelType.ToUpperInvariant() + " is configured but /dev/dri does not exist inside the container.",
                UnraidContext = "On Unraid, /dev/dri must be passed through to the Docker container for Intel/AMD GPU access.",
                FixSteps = new List<string>
                {
                    "In Unraid Docker settings, edit the Jellyfin container",
                    "Add a device mapping: /dev/dri -> /dev/dri",
                    "Apply changes and restart the container",
                    "Verify /dev/dri exists on the Unraid host (ls /dev/dri)"
                }
            });
        }
        else if (!hasRenderNode)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Degraded,
                Category = Category,
                Title = "No render node found in /dev/dri",
                Detail = "/dev/dri exists but no renderD* device was found. Hardware transcoding may not work.",
                UnraidContext = "The render node (e.g., renderD128) is required for VAAPI/QSV encoding. It may be missing if the GPU driver isn't properly loaded on the Unraid host.",
                FixSteps = new List<string>
                {
                    "On the Unraid console, run: ls -la /dev/dri/",
                    "If renderD128 is missing, check that the Intel/AMD GPU driver is loaded",
                    "For Intel iGPU: ensure the i915 module is loaded (modprobe i915)",
                    "Restart the Docker container after verifying the host devices"
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
                Title = "/dev/dri devices found with render node",
                Detail = "GPU render devices are visible inside the container.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }
    }

    private async Task CheckFfmpegEncoders(List<DiagnosticResult> results, string hwAccelType, CancellationToken cancellationToken)
    {
        var encoderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "nvenc", "h264_nvenc" },
            { "vaapi", "h264_vaapi" },
            { "qsv", "h264_qsv" }
        };

        if (!encoderMap.TryGetValue(hwAccelType, out var expectedEncoder))
        {
            return;
        }

        string ffmpegOutput = string.Empty;
        bool ffmpegRan = false;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-encoders",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            ffmpegOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            ffmpegRan = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run ffmpeg -encoders");
        }

        if (!ffmpegRan)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Unknown,
                Category = Category,
                Title = "Could not verify FFmpeg encoders",
                Detail = "Failed to run 'ffmpeg -encoders'. Cannot verify if the required hardware encoder is available.",
                UnraidContext = "Jellyfin's bundled FFmpeg should be available. If this fails, the Jellyfin installation may be incomplete.",
                FixSteps = new List<string>
                {
                    "Verify Jellyfin is using its bundled FFmpeg (check Dashboard > Playback > FFmpeg path)",
                    "If using a custom FFmpeg, ensure it's compiled with the required hardware encoder support"
                }
            });
            return;
        }

        bool hasEncoder = ffmpegOutput.Contains(expectedEncoder, StringComparison.OrdinalIgnoreCase);

        if (!hasEncoder)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "FFmpeg encoder '" + expectedEncoder + "' not found",
                Detail = hwAccelType.ToUpperInvariant() + " is configured but FFmpeg does not list the " + expectedEncoder + " encoder.",
                UnraidContext = "This usually means the FFmpeg binary wasn't compiled with the required hardware encoder support, or the wrong FFmpeg path is configured.",
                FixSteps = new List<string>
                {
                    "Ensure Jellyfin is using its official bundled FFmpeg",
                    "Check Dashboard > Playback > FFmpeg path",
                    "If using linuxserver/jellyfin image, ensure you're on the latest version",
                    "Clear the FFmpeg path field to use the bundled version"
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
                Title = "FFmpeg encoder '" + expectedEncoder + "' is available",
                Detail = "The required hardware encoder for " + hwAccelType.ToUpperInvariant() + " is present in FFmpeg.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }
    }

    private void CheckTranscodeLogs(List<DiagnosticResult> results)
    {
        var patterns = new Dictionary<string, string>
        {
            { "transcode_error", @"(error|fail).*transcode|transcode.*(error|fail)" },
            { "codec_error", @"(Unknown|unsupported).*codec|codec.*(not found|missing)" },
            { "device_denied", @"(permission denied|cannot open).*/dev/(dri|nvidia)" },
            { "hw_accel_fail", @"(hardware acceleration|hwaccel).*(fail|error|unavailable)" }
        };

        var logResults = _logAnalyzer.ScanLogs(patterns);
        int totalErrors = logResults.Values.Sum(v => v.Count);

        if (totalErrors > 0)
        {
            var detail = new List<string>();
            foreach (var (patternName, matches) in logResults)
            {
                if (matches.Count > 0)
                {
                    detail.Add(patternName + ": " + matches.Count + " occurrence(s)");
                }
            }

            results.Add(new DiagnosticResult
            {
                Severity = totalErrors > 10 ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning,
                Status = totalErrors > 10 ? DiagnosticStatus.Broken : DiagnosticStatus.Degraded,
                Category = Category,
                Title = "Transcode errors found in logs (" + totalErrors + " total)",
                Detail = "The following error patterns were found in recent Jellyfin logs:\n" + string.Join("\n", detail),
                UnraidContext = "Transcode errors in Docker often indicate missing device passthrough, incorrect permissions on /dev/dri, or an FFmpeg version mismatch.",
                FixSteps = new List<string>
                {
                    "Check the full Jellyfin log for detailed error messages",
                    "Verify GPU device passthrough in Unraid Docker settings",
                    "Ensure the Jellyfin container user has access to GPU devices",
                    "On Unraid, check that /dev/dri permissions include the container's GID (usually 100)"
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
                Title = "No transcode errors in recent logs",
                Detail = "No hardware acceleration or transcoding errors were found in recent log entries.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add Checkers/HardwareAccelerationChecker.cs
git commit -m "feat: add HardwareAccelerationChecker with GPU, device, FFmpeg, and log detection"
```

---

### Task 6: VolumePathChecker

**Files:**
- Create: `Checkers/VolumePathChecker.cs`

- [ ] **Step 1: Create VolumePathChecker.cs**

```csharp
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
```

- [ ] **Step 2: Commit**

```bash
git add Checkers/VolumePathChecker.cs
git commit -m "feat: add VolumePathChecker with path, mount, and UID/GID detection"
```

---

### Task 7: PermissionsChecker

**Files:**
- Create: `Checkers/PermissionsChecker.cs`

- [ ] **Step 1: Create PermissionsChecker.cs**

```csharp
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
```

- [ ] **Step 2: Commit**

```bash
git add Checkers/PermissionsChecker.cs
git commit -m "feat: add PermissionsChecker with writability tests and log scanning"
```

---

### Task 8: NetworkChecker

**Files:**
- Create: `Checkers/NetworkChecker.cs`

- [ ] **Step 1: Create NetworkChecker.cs**

```csharp
using JellyfinDiagnostics.Models;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class NetworkChecker : IDiagnosticChecker
{
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<NetworkChecker> _logger;

    public string Name => "Networking & Playback";
    public string Category => "Networking";

    public NetworkChecker(
        IServerConfigurationManager configManager,
        ILogger<NetworkChecker> logger)
    {
        _configManager = configManager;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        var config = _configManager.GetNetworkConfiguration();

        CheckHttpsConfiguration(results, config);
        CheckPublishedServerUrl(results, config);
        CheckBitrateCaps(results);
        CheckBaseUrl(results, config);

        return Task.FromResult(results);
    }

    private void CheckHttpsConfiguration(List<DiagnosticResult> results, object networkConfig)
    {
        try
        {
            var configType = networkConfig.GetType();

            bool enableHttps = false;
            string certPath = string.Empty;

            var httpsProperty = configType.GetProperty("EnableHttps");
            if (httpsProperty != null)
            {
                enableHttps = (bool)(httpsProperty.GetValue(networkConfig) ?? false);
            }

            var certProperty = configType.GetProperty("CertificatePath");
            if (certProperty != null)
            {
                certPath = (string)(certProperty.GetValue(networkConfig) ?? string.Empty);
            }

            if (enableHttps && string.IsNullOrEmpty(certPath))
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Broken,
                    Category = Category,
                    Title = "HTTPS enabled but no certificate configured",
                    Detail = "HTTPS is enabled in the server configuration but no certificate path is set. HTTPS connections will fail.",
                    UnraidContext = "On Unraid, it's common to handle HTTPS at the reverse proxy level (Nginx Proxy Manager, SWAG, etc.) rather than in Jellyfin itself. Consider disabling HTTPS in Jellyfin and using a reverse proxy instead.",
                    FixSteps = new List<string>
                    {
                        "Option A: Provide a valid certificate path in Jellyfin Network settings",
                        "Option B (recommended for Unraid): Disable HTTPS in Jellyfin and use a reverse proxy",
                        "If using Nginx Proxy Manager on Unraid, set up a proxy host pointing to Jellyfin's HTTP port",
                        "Set the reverse proxy to handle SSL termination"
                    }
                });
            }
            else if (enableHttps && !string.IsNullOrEmpty(certPath) && !File.Exists(certPath))
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Broken,
                    Category = Category,
                    Title = "HTTPS certificate file not found",
                    Detail = "HTTPS is enabled but the certificate file '" + certPath + "' does not exist inside the container.",
                    UnraidContext = "The certificate path must be accessible inside the Docker container. Ensure the certificate file is volume-mapped into the container.",
                    FixSteps = new List<string>
                    {
                        "Verify the certificate file exists on the Unraid host",
                        "Add a volume mapping in the container settings to make it accessible",
                        "Update the certificate path in Jellyfin to match the container path",
                        "Restart the container"
                    }
                });
            }
            else if (enableHttps)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Info,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = "HTTPS is configured with a certificate",
                    Detail = "HTTPS is enabled and a certificate path is configured.",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
            }
            else
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Info,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = "HTTPS is disabled (HTTP only)",
                    Detail = "Jellyfin is configured to use HTTP only. This is typical when using a reverse proxy for SSL termination.",
                    UnraidContext = "Using a reverse proxy (e.g., Nginx Proxy Manager on Unraid) for HTTPS is the recommended approach.",
                    FixSteps = new List<string>()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check HTTPS configuration");
        }
    }

    private void CheckPublishedServerUrl(List<DiagnosticResult> results, object networkConfig)
    {
        try
        {
            string publishedUrl = string.Empty;
            var configType = networkConfig.GetType();
            var urlProperty = configType.GetProperty("PublishedServerUri")
                              ?? configType.GetProperty("PublishedServerUrl");

            if (urlProperty != null)
            {
                publishedUrl = (string)(urlProperty.GetValue(networkConfig) ?? string.Empty);
            }

            if (string.IsNullOrWhiteSpace(publishedUrl))
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = "Published server URL is not set",
                    Detail = "No published server URL is configured. External clients may not be able to connect properly, especially through a reverse proxy.",
                    UnraidContext = "On Unraid with a reverse proxy, you should set the published server URL to your external domain.",
                    FixSteps = new List<string>
                    {
                        "Go to Jellyfin Dashboard > Networking",
                        "Set 'Published server URI' to your external access URL",
                        "Example: https://jellyfin.yourdomain.com",
                        "Save and restart Jellyfin"
                    }
                });
            }
            else if (publishedUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                     || publishedUrl.Contains("127.0.0.1")
                     || publishedUrl.Contains("0.0.0.0"))
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Broken,
                    Category = Category,
                    Title = "Published server URL points to localhost",
                    Detail = "The published server URL is '" + publishedUrl + "', which will not work for remote clients.",
                    UnraidContext = "Inside a Docker container, localhost refers to the container itself. External clients need your Unraid server's IP or external domain.",
                    FixSteps = new List<string>
                    {
                        "Go to Jellyfin Dashboard > Networking",
                        "Change the published server URL to your Unraid IP (e.g., http://192.168.1.x:8096)",
                        "Or use your external domain if using a reverse proxy",
                        "Save and restart Jellyfin"
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
                    Title = "Published server URL is configured",
                    Detail = "Published server URL is set to '" + publishedUrl + "'.",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check published server URL");
        }
    }

    private void CheckBitrateCaps(List<DiagnosticResult> results)
    {
        try
        {
            var remoteLimit = _configManager.Configuration.RemoteClientBitrateLimit;

            if (remoteLimit > 0 && remoteLimit < 8_000_000)
            {
                double limitMbps = remoteLimit / 1_000_000.0;
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = "Low remote streaming bitrate limit (" + limitMbps.ToString("F1") + " Mbps)",
                    Detail = "The remote streaming bitrate is capped at " + limitMbps.ToString("F1") + " Mbps. This will force transcoding for most 1080p and all 4K content.",
                    UnraidContext = "If your upload speed supports it, consider raising this limit to reduce unnecessary transcoding. Lower limits cause more transcode load on your Unraid server.",
                    FixSteps = new List<string>
                    {
                        "Go to Jellyfin Dashboard > Playback",
                        "Increase the 'Remote streaming bitrate limit'",
                        "For 1080p without transcoding, set to at least 20 Mbps",
                        "For 4K without transcoding, set to at least 80 Mbps",
                        "Or set to 0 for unlimited (clients will direct-play when possible)"
                    }
                });
            }
            else if (remoteLimit == 0)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Info,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = "No remote streaming bitrate limit",
                    Detail = "Remote streaming bitrate is unlimited. Clients will direct-play when possible.",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
            }
            else
            {
                double limitMbps = remoteLimit / 1_000_000.0;
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Info,
                    Status = DiagnosticStatus.Working,
                    Category = Category,
                    Title = "Remote streaming bitrate limit: " + limitMbps.ToString("F1") + " Mbps",
                    Detail = "The remote streaming bitrate is capped at " + limitMbps.ToString("F1") + " Mbps.",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check bitrate caps");
        }
    }

    private void CheckBaseUrl(List<DiagnosticResult> results, object networkConfig)
    {
        try
        {
            string baseUrl = string.Empty;
            var configType = networkConfig.GetType();
            var baseUrlProperty = configType.GetProperty("BaseUrl");

            if (baseUrlProperty != null)
            {
                baseUrl = (string)(baseUrlProperty.GetValue(networkConfig) ?? string.Empty);
            }

            if (!string.IsNullOrEmpty(baseUrl) && !baseUrl.StartsWith('/'))
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = "Base URL does not start with '/'",
                    Detail = "The base URL is set to '" + baseUrl + "' but should start with a forward slash.",
                    UnraidContext = "An incorrect base URL can cause issues with reverse proxy routing on Unraid.",
                    FixSteps = new List<string>
                    {
                        "Go to Jellyfin Dashboard > Networking",
                        "Change the base URL to '/" + baseUrl.TrimStart('/') + "'",
                        "Save and restart Jellyfin"
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check base URL");
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add Checkers/NetworkChecker.cs
git commit -m "feat: add NetworkChecker with HTTPS, published URL, bitrate, and base URL checks"
```

---

### Task 9: DiagnosticsService (Orchestrator)

**Files:**
- Create: `Services/DiagnosticsService.cs`

- [ ] **Step 1: Create DiagnosticsService.cs**

```csharp
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
```

- [ ] **Step 2: Commit**

```bash
git add Services/DiagnosticsService.cs
git commit -m "feat: add DiagnosticsService orchestrator"
```

---

### Task 10: AiIntegrationService

**Files:**
- Create: `Services/AiIntegrationService.cs`

- [ ] **Step 1: Create AiIntegrationService.cs**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using JellyfinDiagnostics.Models;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Services;

public class AiIntegrationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiIntegrationService> _logger;

    public AiIntegrationService(
        IHttpClientFactory httpClientFactory,
        ILogger<AiIntegrationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public DiagnosticsReport SanitizeReport(DiagnosticsReport report)
    {
        var sanitized = new DiagnosticsReport
        {
            Timestamp = report.Timestamp,
            JellyfinVersion = report.JellyfinVersion,
            OperatingSystem = SanitizeOsString(report.OperatingSystem),
            Results = new List<DiagnosticResult>()
        };

        int pathCounter = 0;
        var pathMap = new Dictionary<string, string>();

        foreach (var result in report.Results)
        {
            sanitized.Results.Add(new DiagnosticResult
            {
                Severity = result.Severity,
                Status = result.Status,
                Category = result.Category,
                Title = SanitizeText(result.Title, ref pathCounter, pathMap),
                Detail = SanitizeText(result.Detail, ref pathCounter, pathMap),
                UnraidContext = SanitizeText(result.UnraidContext, ref pathCounter, pathMap),
                FixSteps = result.FixSteps.Select(s => SanitizeText(s, ref pathCounter, pathMap)).ToList()
            });
        }

        return sanitized;
    }

    public async Task<string> SendToAiAsync(
        DiagnosticsReport report,
        string endpointUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var sanitized = SanitizeReport(report);
        var client = _httpClientFactory.CreateClient("DiagnosticsAi");

        var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
        request.Content = JsonContent.Create(new
        {
            model = "default",
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a Jellyfin server diagnostics assistant. Analyze the following diagnostics report from a Jellyfin server running in Docker on Unraid. Provide clear, actionable recommendations. Do not ask for additional information."
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(sanitized, new JsonSerializerOptions { WriteIndented = true })
                }
            }
        });

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Add("Authorization", "Bearer " + apiKey);
        }

        try
        {
            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send diagnostics to AI endpoint");
            throw;
        }
    }

    private static string SanitizeText(string text, ref int pathCounter, Dictionary<string, string> pathMap)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Replace IPv4 addresses
        text = Regex.Replace(text, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "[IP_REDACTED]");

        // Replace MAC addresses
        text = Regex.Replace(text, @"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", "[MAC_REDACTED]");

        // Replace file paths (anonymize) but keep standard system paths
        text = Regex.Replace(text, @"(/[\w./-]+)", match =>
        {
            var path = match.Value;
            if (path.StartsWith("/dev/") || path.StartsWith("/proc/") || path == "/config")
            {
                return path;
            }

            if (!pathMap.TryGetValue(path, out var replacement))
            {
                pathCounter++;
                replacement = "[path_" + pathCounter + "]";
                pathMap[path] = replacement;
            }

            return replacement;
        });

        // Replace usernames in common patterns
        text = Regex.Replace(text, @"\buser[=:]\s*\w+", "[USER_REDACTED]");

        return text;
    }

    private static string SanitizeOsString(string os)
    {
        var match = Regex.Match(os, @"(Linux|Windows|Darwin|FreeBSD)\s+[\d.]+");
        return match.Success ? match.Value : "Linux (version redacted)";
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add Services/AiIntegrationService.cs
git commit -m "feat: add AiIntegrationService with PII stripping and external API integration"
```

---

### Task 11: Plugin Entry, Configuration, API Controller

**Files:**
- Create: `Plugin.cs`
- Create: `PluginConfiguration.cs`
- Create: `Api/DiagnosticsController.cs`

- [ ] **Step 1: Create PluginConfiguration.cs**

```csharp
using MediaBrowser.Model.Plugins;

namespace JellyfinDiagnostics;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableAiIntegration { get; set; } = false;
    public string AiEndpointUrl { get; set; } = string.Empty;
    public string AiApiKey { get; set; } = string.Empty;
    public int LogLinesToScan { get; set; } = 5000;
}
```

- [ ] **Step 2: Create Plugin.cs**

```csharp
using JellyfinDiagnostics.Checkers;
using JellyfinDiagnostics.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinDiagnostics;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "Jellyfin Diagnostics";
    public override string Description => "Admin diagnostics for Docker/Unraid Jellyfin deployments";
    public override Guid Id => new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "JellyfinDiagnostics",
                EmbeddedResourcePath = "JellyfinDiagnostics.Pages.diagnosticsPage.html",
                DisplayName = "Diagnostics"
            }
        };
    }
}

public class DiagnosticsPluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<LogAnalyzer>();
        services.AddSingleton<DiagnosticsService>();
        services.AddSingleton<AiIntegrationService>();

        services.AddSingleton<IDiagnosticChecker, HardwareAccelerationChecker>();
        services.AddSingleton<IDiagnosticChecker, VolumePathChecker>();
        services.AddSingleton<IDiagnosticChecker, PermissionsChecker>();
        services.AddSingleton<IDiagnosticChecker, NetworkChecker>();
    }
}
```

- [ ] **Step 3: Create Api/DiagnosticsController.cs**

```csharp
using System.Net.Mime;
using System.Text.Json;
using JellyfinDiagnostics.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinDiagnostics.Api;

[ApiController]
[Route("Diagnostics")]
[Authorize(Policy = "RequiresElevation")]
public class DiagnosticsController : ControllerBase
{
    private readonly DiagnosticsService _diagnosticsService;
    private readonly AiIntegrationService _aiService;

    public DiagnosticsController(
        DiagnosticsService diagnosticsService,
        AiIntegrationService aiService)
    {
        _diagnosticsService = diagnosticsService;
        _aiService = aiService;
    }

    [HttpGet("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RunDiagnostics(CancellationToken cancellationToken)
    {
        var report = await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);
        return Ok(report);
    }

    [HttpGet("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReport(CancellationToken cancellationToken)
    {
        var report = _diagnosticsService.GetLastReport()
                     ?? await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);
        return Ok(report);
    }

    [HttpGet("Report/Download")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadReport(CancellationToken cancellationToken)
    {
        var report = _diagnosticsService.GetLastReport()
                     ?? await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        return File(bytes, MediaTypeNames.Application.Json, "jellyfin-diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
    }

    [HttpPost("Ai")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendToAi(CancellationToken cancellationToken)
    {
        var pluginInstance = Plugin.Instance;
        if (pluginInstance == null)
        {
            return BadRequest(new { error = "Plugin not initialized" });
        }

        var config = pluginInstance.Configuration;
        if (!config.EnableAiIntegration)
        {
            return BadRequest(new { error = "AI integration is not enabled. Enable it in plugin settings." });
        }

        if (string.IsNullOrEmpty(config.AiEndpointUrl))
        {
            return BadRequest(new { error = "AI endpoint URL is not configured." });
        }

        var report = _diagnosticsService.GetLastReport()
                     ?? await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);

        var aiResponse = await _aiService.SendToAiAsync(
            report,
            config.AiEndpointUrl,
            config.AiApiKey,
            cancellationToken).ConfigureAwait(false);

        return Ok(new { response = aiResponse });
    }
}
```

- [ ] **Step 4: Commit**

```bash
git add Plugin.cs PluginConfiguration.cs Api/DiagnosticsController.cs
git commit -m "feat: add Plugin entry, configuration, and REST API controller"
```

---

### Task 12: Admin Dashboard HTML Page

**Files:**
- Create: `Pages/diagnosticsPage.html`

- [ ] **Step 1: Create diagnosticsPage.html**

The admin dashboard uses safe DOM manipulation (document.createElement, textContent) instead of innerHTML to prevent XSS. It renders diagnostic results as categorized collapsible cards with severity icons, Unraid context, and fix steps.

The full HTML file content is provided in the implementation — it includes:
- Inline CSS following Jellyfin's dark theme
- Summary bar with Critical/Warning/Info counts
- Collapsible category sections built via DOM API
- Export report button (client-side JSON download)
- AI integration button (only visible when enabled)
- AI response modal
- All content rendered using textContent (not innerHTML) for security

- [ ] **Step 2: Commit**

```bash
git add Pages/diagnosticsPage.html
git commit -m "feat: add admin dashboard HTML page with dark theme UI"
```

---

### Task 13: Final Assembly and Verification

- [ ] **Step 1: Verify all files exist**

Run:
```bash
find /home/kasm-user/jellyfin-plugin-diagnostics -name '*.cs' -o -name '*.csproj' -o -name '*.html' -o -name '*.json' -o -name '*.yaml' | sort
```

Expected output should list all 19 files from the file structure.

- [ ] **Step 2: Final commit**

```bash
git add -A
git commit -m "feat: complete Jellyfin AI Diagnostics Plugin v1.0.0"
```
