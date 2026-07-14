using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using JellyfinDiagnostics.Checkers;
using Xunit;

namespace JellyfinDiagnostics.Tests;

/// <summary>
/// IsUserAdmin is reflection-based so that one DLL spans 10.11.0-10.11.11. These
/// tests pin it against the real Jellyfin user entity, which exposes admin state
/// only through its Permissions collection: it has no Policy property, and its
/// HasPermission is a static extension method (invisible to Type.GetMethod).
/// </summary>
public class SecurityCheckerTests
{
    private static User NewUser(bool isAdmin)
    {
        var user = new User("bob", "prov", "prov");
        user.AddDefaultPermissions();
        user.SetPermission(PermissionKind.IsAdministrator, isAdmin);
        return user;
    }

    [Fact]
    public void IsUserAdmin_True_ForRealAdministratorEntity()
    {
        Assert.True(SecurityChecker.IsUserAdmin(NewUser(true)));
    }

    [Fact]
    public void IsUserAdmin_False_ForRealNonAdministratorEntity()
    {
        Assert.False(SecurityChecker.IsUserAdmin(NewUser(false)));
    }

    [Fact]
    public void IsUserAdmin_False_ForUnknownShape()
    {
        Assert.False(SecurityChecker.IsUserAdmin(new object()));
    }

    // The older API shape the existing reflection already handled.
    private sealed class LegacyPolicy
    {
        public bool IsAdministrator { get; init; }
    }

    private sealed class LegacyUser
    {
        public LegacyPolicy Policy { get; init; } = new();
    }

    [Fact]
    public void IsUserAdmin_StillHonoursLegacyPolicyShape()
    {
        Assert.True(SecurityChecker.IsUserAdmin(new LegacyUser { Policy = new LegacyPolicy { IsAdministrator = true } }));
        Assert.False(SecurityChecker.IsUserAdmin(new LegacyUser()));
    }

    /// <summary>
    /// CheckRemoteAccessSafety used to read EnableRemoteAccess off ServerConfiguration,
    /// where it has never existed on any supported release. The reflection lookup returned
    /// null, remoteAccess stayed false, and the "remote access without HTTPS" warning could
    /// never fire. It must be read from NetworkConfiguration. This pins that.
    /// </summary>
    [Fact]
    public void EnableRemoteAccess_LivesOnNetworkConfiguration_NotServerConfiguration()
    {
        Assert.NotNull(typeof(MediaBrowser.Common.Net.NetworkConfiguration).GetProperty("EnableRemoteAccess"));
        Assert.Null(typeof(MediaBrowser.Model.Configuration.ServerConfiguration).GetProperty("EnableRemoteAccess"));
    }
}
