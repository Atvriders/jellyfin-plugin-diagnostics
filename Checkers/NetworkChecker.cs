using JellyfinDiagnostics.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class NetworkChecker : IDiagnosticChecker
{
    private readonly IConfigurationManager _configManager;
    private readonly IServerConfigurationManager _serverConfigManager;
    private readonly ILogger<NetworkChecker> _logger;

    public string Name => "Networking & Playback";
    public string Category => "Networking";

    public NetworkChecker(
        IConfigurationManager configManager,
        IServerConfigurationManager serverConfigManager,
        ILogger<NetworkChecker> logger)
    {
        _configManager = configManager;
        _serverConfigManager = serverConfigManager;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        NetworkConfiguration? networkConfig = null;
        try
        {
            networkConfig = _configManager.GetConfiguration<NetworkConfiguration>("network");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load network configuration");
        }

        if (networkConfig == null)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Unknown,
                Category = Category,
                Title = "Network configuration not available",
                Detail = "Could not load Jellyfin network configuration.",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
            return Task.FromResult(results);
        }

        CheckHttpsConfiguration(results, networkConfig);
        CheckPublishedServerUrls(results, networkConfig);
        CheckBaseUrl(results, networkConfig);
        CheckBitrateCaps(results);

        return Task.FromResult(results);
    }

    private void CheckHttpsConfiguration(List<DiagnosticResult> results, NetworkConfiguration config)
    {
        if (config.EnableHttps && string.IsNullOrEmpty(config.CertificatePath))
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "HTTPS enabled but no certificate configured",
                Detail = "HTTPS is enabled but no certificate path is set. HTTPS connections will fail.",
                UnraidContext = "On Unraid, handle HTTPS at the reverse proxy level (Nginx Proxy Manager, SWAG) rather than in Jellyfin. This is the recommended approach.",
                FixSteps = new List<string>
                {
                    "Option A (recommended): Disable HTTPS in Jellyfin and use Nginx Proxy Manager or SWAG on Unraid",
                    "Option B: Provide a valid certificate path in Jellyfin Network settings (must be .pfx file mapped into the container)"
                }
            });
        }
        else if (config.EnableHttps && !File.Exists(config.CertificatePath))
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Critical,
                Status = DiagnosticStatus.Broken,
                Category = Category,
                Title = "HTTPS certificate file not found",
                Detail = "Certificate file '" + config.CertificatePath + "' does not exist inside the container.",
                UnraidContext = "The certificate file must be mapped into the Docker container via a volume mount.",
                FixSteps = new List<string>
                {
                    "Verify the certificate file exists on the Unraid host",
                    "Add a volume mapping in the container settings to make the cert accessible",
                    "Update the certificate path in Jellyfin to match the container path",
                    "Restart the container"
                }
            });
        }
        else if (config.EnableHttps)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = "HTTPS is configured with a certificate",
                Detail = "HTTPS enabled with certificate at '" + config.CertificatePath + "'.",
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
                Detail = "Jellyfin uses HTTP only. Typical when using a reverse proxy for SSL termination.",
                UnraidContext = "Recommended: use Nginx Proxy Manager or SWAG on Unraid to handle HTTPS.",
                FixSteps = new List<string>()
            });
        }
    }

    private void CheckPublishedServerUrls(List<DiagnosticResult> results, NetworkConfiguration config)
    {
        var subnetUris = config.PublishedServerUriBySubnet ?? Array.Empty<string>();

        if (subnetUris.Length == 0)
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = "No published server URIs configured",
                Detail = "No per-subnet published server URIs are set. Jellyfin will auto-detect addresses.",
                UnraidContext = "On Unraid with a reverse proxy, consider setting a published URI per subnet: e.g., 'external=https://jellyfin.yourdomain.com' for WAN clients and 'lan=http://192.168.1.x:8096' for LAN.",
                FixSteps = new List<string>()
            });
            return;
        }

        foreach (var entry in subnetUris)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var parts = entry.Split('=', 2);
            var url = parts.Length == 2 ? parts[1] : entry;

            if (url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || url.Contains("127.0.0.1")
                || url.Contains("0.0.0.0"))
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Broken,
                    Category = Category,
                    Title = "Published URI points to localhost: " + entry,
                    Detail = "A published server URI is '" + entry + "'. localhost inside a Docker container refers to the container itself, not reachable from clients.",
                    UnraidContext = "Use your Unraid server LAN IP (e.g., http://192.168.1.x:8096) or external domain instead.",
                    FixSteps = new List<string>
                    {
                        "Dashboard > Networking > 'Published server URIs per subnet'",
                        "Replace the localhost entry with your LAN IP or external domain",
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
                    Title = "Published URI: " + entry,
                    Detail = "Published server URI entry is configured.",
                    UnraidContext = string.Empty,
                    FixSteps = new List<string>()
                });
            }
        }
    }

    private void CheckBaseUrl(List<DiagnosticResult> results, NetworkConfiguration config)
    {
        var baseUrl = config.BaseUrl ?? string.Empty;

        if (!string.IsNullOrEmpty(baseUrl) && !baseUrl.StartsWith('/'))
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Warning,
                Status = DiagnosticStatus.Degraded,
                Category = Category,
                Title = "Base URL does not start with '/'",
                Detail = "Base URL is '" + baseUrl + "' but should start with a forward slash.",
                UnraidContext = "Incorrect base URL causes reverse proxy routing failures.",
                FixSteps = new List<string>
                {
                    "Dashboard > Networking",
                    "Change base URL to '/" + baseUrl.TrimStart('/') + "'",
                    "Save and restart Jellyfin"
                }
            });
        }
        else if (!string.IsNullOrEmpty(baseUrl))
        {
            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = "Base URL: " + baseUrl,
                Detail = "Base URL is configured (for reverse-proxy subpath mounting).",
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });
        }
    }

    private void CheckBitrateCaps(List<DiagnosticResult> results)
    {
        try
        {
            var remoteLimit = _serverConfigManager.Configuration.RemoteClientBitrateLimit;

            if (remoteLimit > 0 && remoteLimit < 8_000_000)
            {
                double limitMbps = remoteLimit / 1_000_000.0;
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = "Low remote streaming bitrate limit (" + limitMbps.ToString("F1") + " Mbps)",
                    Detail = "Remote streaming bitrate is capped at " + limitMbps.ToString("F1") + " Mbps. This forces transcoding for most 1080p and all 4K content.",
                    UnraidContext = "Lower limits cause more transcode load on your Unraid server. If upload bandwidth allows, raise this.",
                    FixSteps = new List<string>
                    {
                        "Dashboard > Playback",
                        "For 1080p direct play, set >= 20 Mbps",
                        "For 4K direct play, set >= 80 Mbps",
                        "Or set 0 for unlimited"
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
                    Detail = "Clients will direct-play when possible.",
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
                    Detail = "Remote streaming bitrate capped at " + limitMbps.ToString("F1") + " Mbps.",
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
}
