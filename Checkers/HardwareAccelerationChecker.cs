using System.Diagnostics;
using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class HardwareAccelerationChecker : IDiagnosticChecker
{
    private readonly IConfigurationManager _configManager;
    private readonly LogAnalyzer _logAnalyzer;
    private readonly ILogger<HardwareAccelerationChecker> _logger;

    public string Name => "Hardware Acceleration";
    public string Category => "Hardware Acceleration";

    public HardwareAccelerationChecker(
        IConfigurationManager configManager,
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
        var hwAccelType = encodingOptions.HardwareAccelerationType;

        if (hwAccelType == HardwareAccelerationType.none)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = "Hardware acceleration is disabled",
                Detail = "No hardware acceleration is configured. Transcoding will use CPU only.",
                UnraidContext = "This is fine if you don't need transcoding. To enable GPU transcoding on Unraid, pass a GPU device through to the Docker container.",
                FixSteps = new List<string>
                {
                    "In Unraid Docker settings, edit the Jellyfin container",
                    "Intel/AMD: add device /dev/dri -> /dev/dri",
                    "NVIDIA: install 'Nvidia-Driver' plugin, add '--runtime=nvidia' to Extra Parameters, set NVIDIA_VISIBLE_DEVICES=all",
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
            UnraidContext = "Verify the corresponding GPU device is passed through to the Docker container.",
            FixSteps = new List<string>()
        });

        switch (hwAccelType)
        {
            case HardwareAccelerationType.nvenc:
                await CheckNvidiaDevices(results, cancellationToken).ConfigureAwait(false);
                break;
            case HardwareAccelerationType.vaapi:
            case HardwareAccelerationType.qsv:
                CheckDriDevices(results, hwAccelType);
                break;
            case HardwareAccelerationType.amf:
                CheckDriDevices(results, hwAccelType);
                break;
        }

        await CheckFfmpegEncoders(results, hwAccelType, encodingOptions.EncoderAppPath, cancellationToken).ConfigureAwait(false);
        CheckTranscodeLogs(results);

        return results;
    }

    private async Task CheckNvidiaDevices(List<DiagnosticResult> results, CancellationToken cancellationToken)
    {
        bool hasNvidiaCtl = File.Exists("/dev/nvidiactl");
        bool hasNvidia0 = File.Exists("/dev/nvidia0");
        bool hasNvidiaUvm = File.Exists("/dev/nvidia-uvm");

        if (!hasNvidiaCtl && !hasNvidia0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "NVIDIA device nodes not found",
                Detail = "NVENC is configured but /dev/nvidiactl and /dev/nvidia0 are not visible inside the container.",
                UnraidContext = "Unraid NVIDIA passthrough requires the Nvidia-Driver plugin and NVIDIA container runtime. Without these, the GPU is invisible inside Docker.",
                FixSteps = new List<string>
                {
                    "Install 'Nvidia-Driver' plugin from Unraid Community Applications",
                    "Reboot Unraid to load the driver",
                    "Edit the Jellyfin container, add '--runtime=nvidia' to Extra Parameters",
                    "Add environment variable NVIDIA_VISIBLE_DEVICES=all",
                    "Add environment variable NVIDIA_DRIVER_CAPABILITIES=compute,video,utility",
                    "Apply and restart the container"
                }
            });
        }
        else
        {
            var missing = new List<string>();
            if (!hasNvidiaUvm) missing.Add("/dev/nvidia-uvm");
            var detail = "NVIDIA device nodes found: " + (hasNvidiaCtl ? "/dev/nvidiactl " : "") + (hasNvidia0 ? "/dev/nvidia0 " : "") + (hasNvidiaUvm ? "/dev/nvidia-uvm" : "");

            results.Add(new DiagnosticResult
            {
                Severity = missing.Count > 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                Status = missing.Count > 0 ? DiagnosticStatus.Degraded : DiagnosticStatus.Working,
                Category = Category,
                Title = missing.Count > 0 ? "NVIDIA devices partially present" : "NVIDIA device nodes found",
                Detail = detail,
                UnraidContext = missing.Count > 0 ? "Missing UVM device can cause CUDA init failures. Ensure NVIDIA_DRIVER_CAPABILITIES includes 'compute'." : string.Empty,
                FixSteps = missing.Count > 0 ? new List<string> { "Set NVIDIA_DRIVER_CAPABILITIES=compute,video,utility in the container env" } : new List<string>()
            });
        }

        bool nvidiaSmiAvailable = false;
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
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
                Title = "nvidia-smi not available or failed",
                Detail = "nvidia-smi is not available inside the container or returned an error.",
                UnraidContext = "nvidia-smi availability depends on the container runtime being set to 'nvidia'. Transcoding may still work if device nodes are present.",
                FixSteps = new List<string>
                {
                    "Verify '--runtime=nvidia' is in the container's Extra Parameters",
                    "Ensure the Nvidia-Driver plugin matches your Unraid version",
                    "Reboot Unraid after plugin updates"
                }
            });
        }
    }

    private void CheckDriDevices(List<DiagnosticResult> results, HardwareAccelerationType hwAccelType)
    {
        bool hasDriDir = Directory.Exists("/dev/dri");
        bool hasRenderD128 = File.Exists("/dev/dri/renderD128");

        if (!hasDriDir)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "/dev/dri not found",
                Detail = hwAccelType + " is configured but /dev/dri does not exist inside the container.",
                UnraidContext = "/dev/dri must be passed through to the Jellyfin Docker container for Intel/AMD GPU access.",
                FixSteps = new List<string>
                {
                    "In Unraid, edit the Jellyfin Docker container",
                    "Add a device mapping: /dev/dri -> /dev/dri",
                    "Apply and restart the container",
                    "Verify /dev/dri exists on the Unraid host first (ls /dev/dri)"
                }
            });
        }
        else if (!hasRenderD128)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Degraded,
                Category = Category,
                Title = "renderD128 missing from /dev/dri",
                Detail = "/dev/dri exists but /dev/dri/renderD128 is missing. Hardware transcoding requires the render node.",
                UnraidContext = "The render node is required for VAAPI/QSV encoding. It may be missing if the GPU driver isn't loaded on the Unraid host.",
                FixSteps = new List<string>
                {
                    "On Unraid console: ls -la /dev/dri/",
                    "If renderD128 missing, check GPU driver: lsmod | grep -E 'i915|amdgpu|radeon'",
                    "For Intel iGPU ensure i915 module is loaded",
                    "Restart the container after verifying host devices"
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
                Title = "/dev/dri/renderD128 present",
                Detail = "GPU render node is accessible inside the container.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }
    }

    private async Task CheckFfmpegEncoders(List<DiagnosticResult> results, HardwareAccelerationType hwAccelType, string? ffmpegPath, CancellationToken cancellationToken)
    {
        string? expectedEncoder = hwAccelType switch
        {
            HardwareAccelerationType.nvenc => "h264_nvenc",
            HardwareAccelerationType.vaapi => "h264_vaapi",
            HardwareAccelerationType.qsv => "h264_qsv",
            HardwareAccelerationType.amf => "h264_amf",
            HardwareAccelerationType.videotoolbox => "h264_videotoolbox",
            HardwareAccelerationType.v4l2m2m => "h264_v4l2m2m",
            HardwareAccelerationType.rkmpp => "h264_rkmpp",
            _ => null
        };

        if (expectedEncoder == null)
        {
            return;
        }

        var ffmpegExe = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
        string ffmpegOutput = string.Empty;
        bool ffmpegRan = false;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = "-hide_banner -encoders",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            ffmpegOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            ffmpegRan = process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run {FFmpeg} -encoders", ffmpegExe);
        }

        if (!ffmpegRan)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Unknown,
                Category = Category,
                Title = "Could not verify FFmpeg encoders",
                Detail = "Failed to run '" + ffmpegExe + " -encoders'. Cannot verify hardware encoder availability.",
                UnraidContext = "Jellyfin's bundled FFmpeg (jellyfin-ffmpeg) is used by default. If a custom path is set, it must exist and be executable inside the container.",
                FixSteps = new List<string>
                {
                    "Check Dashboard > Playback > FFmpeg path",
                    "Clear the field to use bundled jellyfin-ffmpeg",
                    "If using linuxserver/jellyfin, ensure the image is up to date"
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
                Detail = hwAccelType + " is configured but FFmpeg does not list the " + expectedEncoder + " encoder.",
                UnraidContext = "jellyfin-ffmpeg includes all hardware encoders. If missing, a custom FFmpeg path without GPU support is likely configured.",
                FixSteps = new List<string>
                {
                    "Clear the FFmpeg path in Dashboard > Playback to use bundled jellyfin-ffmpeg",
                    "Update to the latest linuxserver/jellyfin or jellyfin/jellyfin image"
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
                Detail = "The required hardware encoder is present in FFmpeg.",
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
                Detail = "Error patterns found in recent logs:\n" + string.Join("\n", detail),
                UnraidContext = "Transcode errors on Unraid Docker commonly mean missing device passthrough, /dev/dri permission issues, or a stale container image.",
                FixSteps = new List<string>
                {
                    "Check the full Jellyfin log for detailed error messages",
                    "Verify GPU device passthrough in Unraid Docker settings",
                    "On Unraid, check /dev/dri permissions include the container's GID (usually 100)",
                    "For Intel iGPU, set group_add: [100] or add --group-add=video"
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
                Detail = "No hardware acceleration or transcoding errors found in recent log entries.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }
    }
}
