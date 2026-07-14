using System.Collections;
using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class SecurityChecker : IDiagnosticChecker
{
    private readonly IConfigurationManager _configManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<SecurityChecker> _logger;

    public string Name => "Security";
    public string Category => "Security";

    public SecurityChecker(
        IConfigurationManager configManager,
        IUserManager userManager,
        ILogger<SecurityChecker> logger)
    {
        _configManager = configManager;
        _userManager = userManager;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        var adminCountBefore = results.Count;
        int totalUsers = CountUsers();
        int adminUsers = CountAdmins();
        CheckDefaultAdmin(results);
        CheckRemoteAccessSafety(results);
        CheckUpnpEnabled(results);

        // Always emit a baseline posture summary so the Security category is visible
        // even when every sub-check is happy.
        results.Insert(0, new DiagnosticResult
        {
            Severity = DiagnosticSeverity.Info,
            Status = DiagnosticStatus.Working,
            Category = Category,
            Title = "Security posture: " + totalUsers + " user(s), " + adminUsers + " admin(s)",
            Detail = "Baseline: checked admin usernames, remote access / HTTPS combo, and UPnP port mapping.",
            UnraidContext = string.Empty,
            FixSteps = new List<string>()
        });

        return Task.FromResult(results);
    }

    private int CountUsers()
    {
        try
        {
            return UserEnumerator.GetUsers(_userManager).Count;
        }
        catch
        {
            return 0;
        }
    }

    private int CountAdmins()
    {
        try
        {
            int n = 0;
            foreach (var u in UserEnumerator.GetUsers(_userManager))
            {
                if (IsUserAdmin(u))
                {
                    n++;
                }
            }

            return n;
        }
        catch
        {
            return 0;
        }
    }

    private void CheckDefaultAdmin(List<DiagnosticResult> results)
    {
        try
        {
            foreach (var user in UserEnumerator.GetUsers(_userManager))
            {
                // Reflect on the user to find an admin flag; property location varies
                // between Jellyfin versions (Policy.IsAdministrator, direct HasPermission, etc.).
                bool isAdmin = IsUserAdmin(user);
                if (!isAdmin)
                {
                    continue;
                }

                // The element type is object (user enumeration is resolved at runtime to
                // span 10.11.0-10.11.11), so the username is read reflectively too.
                var name = user.GetType().GetProperty("Username")?.GetValue(user) as string ?? string.Empty;
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

    internal static bool IsUserAdmin(object user)
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

            // Try an instance HasPermission(PermissionKind.IsAdministrator) via reflection
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

            // The shape every 10.11.x server actually has. The user entity carries neither
            // a Policy property nor an instance HasPermission (HasPermission is a static
            // extension on Jellyfin.Data.UserEntityExtensions, which Type.GetMethod cannot
            // see), so admin state is read straight off the Permissions collection.
            if (type.GetProperty("Permissions")?.GetValue(user) is IEnumerable permissions)
            {
                foreach (var permission in permissions)
                {
                    if (permission == null)
                    {
                        continue;
                    }

                    var permissionType = permission.GetType();
                    var kind = permissionType.GetProperty("Kind")?.GetValue(permission);
                    if (kind?.ToString() != "IsAdministrator")
                    {
                        continue;
                    }

                    return (bool)(permissionType.GetProperty("Value")?.GetValue(permission) ?? false);
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

            // EnableRemoteAccess lives on NetworkConfiguration, not ServerConfiguration, in
            // every supported release (10.11.0-10.11.11). Reading it off ServerConfiguration
            // silently yielded false forever, so this warning could never fire.
            bool remoteAccess = netConfig.EnableRemoteAccess;

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
