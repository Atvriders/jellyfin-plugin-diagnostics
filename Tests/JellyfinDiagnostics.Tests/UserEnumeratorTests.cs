using JellyfinDiagnostics.Services;
using Xunit;

namespace JellyfinDiagnostics.Tests;

public class UserEnumeratorTests
{
    // Mimics Jellyfin <= 10.11.6, which exposes a Users property.
    private sealed class OldStyleUserManager
    {
        public IEnumerable<object> Users => new object[] { "alice", "bob" };
    }

    // Mimics Jellyfin >= 10.11.11, which exposes GetUsers().
    private sealed class NewStyleUserManager
    {
        public IEnumerable<object> GetUsers() => new object[] { "carol" };
    }

    private sealed class UnknownUserManager
    {
    }

    // A server whose Users property blows up must not take the checker down with it.
    private sealed class ThrowingUserManager
    {
        public IEnumerable<object> Users => throw new InvalidOperationException("db is gone");
    }

    // Nulls in the sequence are skipped rather than counted.
    private sealed class NullElementUserManager
    {
        public IEnumerable<object?> GetUsers() => new object?[] { "dave", null };
    }

    [Fact]
    public void GetUsers_ReadsUsersProperty_OnOldApi()
    {
        Assert.Equal(2, UserEnumerator.GetUsers(new OldStyleUserManager()).Count);
    }

    [Fact]
    public void GetUsers_CallsGetUsersMethod_OnNewApi()
    {
        Assert.Single(UserEnumerator.GetUsers(new NewStyleUserManager()));
    }

    [Fact]
    public void GetUsers_ReturnsEmpty_WhenNeitherApiExists()
    {
        Assert.Empty(UserEnumerator.GetUsers(new UnknownUserManager()));
    }

    [Fact]
    public void GetUsers_ReturnsEmpty_WhenNull()
    {
        Assert.Empty(UserEnumerator.GetUsers(null!));
    }

    [Fact]
    public void GetUsers_ReturnsEmpty_WhenApiThrows()
    {
        Assert.Empty(UserEnumerator.GetUsers(new ThrowingUserManager()));
    }

    [Fact]
    public void GetUsers_SkipsNullElements()
    {
        Assert.Single(UserEnumerator.GetUsers(new NullElementUserManager()));
    }

    [Fact]
    public void GetUsers_PreservesElements()
    {
        Assert.Equal(new object[] { "alice", "bob" }, UserEnumerator.GetUsers(new OldStyleUserManager()));
    }
}
