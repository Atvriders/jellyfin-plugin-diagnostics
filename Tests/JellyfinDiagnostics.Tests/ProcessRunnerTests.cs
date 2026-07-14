using System.Diagnostics;
using JellyfinDiagnostics.Services;
using Xunit;

namespace JellyfinDiagnostics.Tests;

/// <summary>
/// The diagnostics checkers shell out to sqlite3 (PRAGMA quick_check) and to `which`.
/// The original code read stdout with a blocking ReadToEnd() BEFORE calling
/// WaitForExit(10000), which made the timeout unreachable: ReadToEnd only returns at
/// stdout EOF, i.e. once the child has already exited. On the exact machine this checker
/// warns about - a multi-GB library.db on an Unraid spinning array - quick_check runs for
/// minutes and GET /Diagnostics/Run hung for all of them.
///
/// These tests pin the two properties that were missing: the timeout is real, and a child
/// that floods stderr does not deadlock.
/// </summary>
public class ProcessRunnerTests
{
    private static bool HasShell => File.Exists("/bin/sh");

    [Fact]
    public async Task RunAsync_CapturesOutputAndExitCode()
    {
        if (!HasShell)
        {
            return;
        }

        var result = await ProcessRunner.RunAsync(
            "/bin/sh",
            new[] { "-c", "echo ok; echo boom >&2; exit 3" },
            5000,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.ExitCode);
        Assert.Equal("ok", result.StandardOutput.Trim());
        Assert.Equal("boom", result.StandardError.Trim());
    }

    [Fact]
    public async Task RunAsync_KillsTheChild_AndReturnsNull_WhenItOverrunsTheTimeout()
    {
        if (!HasShell)
        {
            return;
        }

        // Holds stdout open for 30s, exactly like a long quick_check: the pre-fix code
        // would block in ReadToEnd() for the whole 30 seconds and never honour the cap.
        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(
            "/bin/sh",
            new[] { "-c", "sleep 30" },
            1500,
            CancellationToken.None);
        stopwatch.Stop();

        Assert.Null(result);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(12),
            $"The timeout was not enforced: the run took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_DoesNotDeadlock_WhenTheChildFloodsStderr()
    {
        if (!HasShell)
        {
            return;
        }

        // ~200 KB of stderr: far past the ~64 KB pipe buffer. A child whose stderr is
        // redirected but never drained blocks on write() forever.
        var result = await ProcessRunner.RunAsync(
            "/bin/sh",
            new[] { "-c", "dd if=/dev/zero bs=1024 count=200 2>/dev/null | tr '\\0' 'x' >&2; echo done" },
            10000,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result!.ExitCode);
        Assert.Equal("done", result.StandardOutput.Trim());
        Assert.True(result.StandardError.Length >= 200 * 1024, $"Only {result.StandardError.Length} stderr bytes were drained.");
    }

    [Fact]
    public async Task RunAsync_HonoursCallerCancellation()
    {
        if (!HasShell)
        {
            return;
        }

        using var cts = new CancellationTokenSource(500);

        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "sleep 30" }, 60000, cts.Token);
        stopwatch.Stop();

        Assert.Null(result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(12), $"Cancellation was ignored: the run took {stopwatch.Elapsed}.");
    }
}
