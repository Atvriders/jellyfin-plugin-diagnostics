using System.Collections;

namespace JellyfinDiagnostics.Services;

/// <summary>
/// Enumerates Jellyfin users across incompatible 10.11.x APIs.
/// 10.11.0-10.11.6 expose an IUserManager.Users property; 10.11.11 replaced it
/// with GetUsers(). The plugin ships one DLL for the whole line, so the call
/// must be resolved at runtime rather than bound at compile time.
/// </summary>
public static class UserEnumerator
{
    /// <summary>
    /// Returns every user the supplied user manager exposes, or an empty list if
    /// neither API shape is present. Never throws.
    /// </summary>
    /// <param name="userManager">The Jellyfin IUserManager, passed loosely typed.</param>
    /// <returns>The users, as loosely typed objects.</returns>
    public static IReadOnlyList<object> GetUsers(object userManager)
    {
        if (userManager == null)
        {
            return Array.Empty<object>();
        }

        var type = userManager.GetType();

        object? raw = null;
        try
        {
            var method = type.GetMethod("GetUsers", Type.EmptyTypes);
            if (method != null)
            {
                raw = method.Invoke(userManager, null);
            }
            else
            {
                raw = type.GetProperty("Users")?.GetValue(userManager);
            }
        }
        catch
        {
            return Array.Empty<object>();
        }

        if (raw is not IEnumerable enumerable)
        {
            return Array.Empty<object>();
        }

        var users = new List<object>();
        try
        {
            foreach (var user in enumerable)
            {
                if (user != null)
                {
                    users.Add(user);
                }
            }
        }
        catch
        {
            // A lazily evaluated sequence can still blow up mid-enumeration.
            return users;
        }

        return users;
    }
}
