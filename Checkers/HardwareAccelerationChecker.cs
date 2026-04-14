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
