using JellyfinDiagnostics.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class SecurityChecker : IDiagnosticChecker
{
    private readonly IServerConfigurationManager _serverConfigManager;
    private readonly IConfigurationManager _configManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<SecurityChecker> _logger;

    public string Name => "Security";
    public string Category => "Security";

    public SecurityChecker(
        IServerConfigurationManager serverConfigManager,
        IConfigurationManager configManager,
        IUserManager userManager,
        ILogger<SecurityChecker> logger)
    {
        _serverConfigManager = serverConfigManager;
        _configManager = configManager;
        _userManager = userManager;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        CheckDefaultAdmin(results);
        CheckRemoteAccessSafety(results);
        CheckUpnpEnabled(results);

        return Task.FromResult(results);
    }

    private void CheckDefaultAdmin(List<DiagnosticResult> results)
    {
        try
        {
            var users = _userManager.Users;
            foreach (var user in users)
            {
                // Reflect on the user to find an admin flag; property location varies
                // between Jellyfin versions (Policy.IsAdministrator, direct HasPermission, etc.).
                bool isAdmin = IsUserAdmin(user);
                if (!isAdmin)
                {
                    continue;
                }

                var name = user.Username ?? string.Empty;
                var nameLower = name.ToLowerInvariant();
                if (nameLower == "admin" || nameLower == "administrator" || nameLower == "jellyfin" || nameLower == "root")
                {
                    results.Add(new DiagnosticResult
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Status = DiagnosticStatus.Degraded,
                        Category = Category,
                        Title = "Admin user has a generic name: " + name,
                        Detail = "A generic admin username makes credential-stuffing attacks easier.",
                        UnraidContext = "If Jellyfin is exposed to the internet (even behind a reverse proxy), bots WILL try admin/admin, administrator/password, etc. Use a non-obvious admin username.",
                        FixSteps = new List<string>
                        {
                            "Dashboard > Users > click the admin user > rename",
                            "Use a strong, unique password",
                            "Enable 2FA if available through a plugin"
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check admin users");
        }
    }

    private static bool IsUserAdmin(object user)
    {
        try
        {
            var type = user.GetType();

            // Try Policy.IsAdministrator (older Jellyfin API shape)
            var policyProp = type.GetProperty("Policy");
            if (policyProp != null)
            {
                var policy = policyProp.GetValue(user);
                if (policy != null)
                {
                    var isAdminProp = policy.GetType().GetProperty("IsAdministrator");
                    if (isAdminProp != null)
                    {
                        return (bool)(isAdminProp.GetValue(policy) ?? false);
                    }
                }
            }

            // Try HasPermission(PermissionKind.IsAdministrator) via reflection
            var hasPermMethod = type.GetMethod("HasPermission", new[] { typeof(int) })
                              ?? type.GetMethods()
                                     .FirstOrDefault(m => m.Name == "HasPermission" && m.GetParameters().Length == 1);
            if (hasPermMethod != null)
            {
                var paramType = hasPermMethod.GetParameters()[0].ParameterType;
                if (paramType.IsEnum)
                {
                    foreach (var v in Enum.GetValues(paramType))
                    {
                        if (v.ToString() == "IsAdministrator")
                        {
                            return (bool)(hasPermMethod.Invoke(user, new[] { v }) ?? false);
                        }
                    }
                }
            }
        }
        catch
        {
            // Best-effort
        }
        return false;
    }

    private void CheckRemoteAccessSafety(List<DiagnosticResult> results)
    {
        try
        {
            NetworkConfiguration? netConfig = null;
            try
            {
                netConfig = _configManager.GetConfiguration<NetworkConfiguration>("network");
            }
            catch { }

            if (netConfig == null)
            {
                return;
            }

            var serverConfig = _serverConfigManager.Configuration;

            bool remoteAccess = false;
            try
            {
                var type = serverConfig.GetType();
                var prop = type.GetProperty("EnableRemoteAccess");
                if (prop != null)
                {
                    remoteAccess = (bool)(prop.GetValue(serverConfig) ?? false);
                }
            }
            catch { }

            if (remoteAccess && !netConfig.EnableHttps)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = "Remote access enabled without HTTPS",
                    Detail = "Jellyfin allows remote connections but HTTPS is disabled. Credentials are transmitted in cleartext if anyone accesses Jellyfin directly over HTTP from outside.",
                    UnraidContext = "The standard Unraid pattern is to keep Jellyfin on HTTP internally and put a reverse proxy (Nginx Proxy Manager, SWAG) in front for HTTPS. That is fine AS LONG AS the HTTP port is not exposed to the internet directly. Confirm your firewall only forwards to the reverse proxy.",
                    FixSteps = new List<string>
                    {
                        "Option A: put Jellyfin behind a reverse proxy (NPM or SWAG on Unraid) with Let's Encrypt",
                        "Option B: enable HTTPS in Jellyfin with a valid certificate path",
                        "Check your router/firewall: port 8096 should NOT be forwarded to Jellyfin directly",
                        "Only the reverse proxy port (443) should be internet-facing"
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote access check failed");
        }
    }

    private void CheckUpnpEnabled(List<DiagnosticResult> results)
    {
        try
        {
            NetworkConfiguration? netConfig = null;
            try { netConfig = _configManager.GetConfiguration<NetworkConfiguration>("network"); }
            catch { }
            if (netConfig == null) return;

            bool upnp = false;
            try
            {
                var type = netConfig.GetType();
                var prop = type.GetProperty("EnableUPnP");
                if (prop != null)
                {
                    upnp = (bool)(prop.GetValue(netConfig) ?? false);
                }
            }
            catch { }

            if (upnp)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = "UPnP port mapping is enabled",
                    Detail = "Jellyfin is configured to open ports on your router via UPnP. This bypasses your firewall and exposes the server to the internet without a reverse proxy.",
                    UnraidContext = "Almost no Unraid user wants this. The recommended approach is a reverse proxy with HTTPS. Disable UPnP in Jellyfin and handle external access through NPM or SWAG.",
                    FixSteps = new List<string>
                    {
                        "Dashboard > Networking > disable 'Enable UPnP'",
                        "Also check your router UPnP logs and remove any stale Jellyfin entries",
                        "Set up a reverse proxy for external access instead"
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UPnP check failed");
        }
    }
}
