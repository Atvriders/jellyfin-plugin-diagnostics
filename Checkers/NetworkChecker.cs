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
