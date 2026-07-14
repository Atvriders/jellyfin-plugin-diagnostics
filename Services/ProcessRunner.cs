using System.Diagnostics;

namespace JellyfinDiagnostics.Services;

/// <summary>The captured result of a child process that exited within its time budget.</summary>
public sealed class ProcessResult
{
    /// <summary>Creates a result from a child process that exited on its own.</summary>
    public ProcessResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    /// <summary>The process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Everything the process wrote to stdout.</summary>
    public string StandardOutput { get; }

    /// <summary>Everything the process wrote to stderr (drained, so it cannot deadlock).</summary>
    public string StandardError { get; }
}

/// <summary>
/// Runs a short-lived child process under a hard wall-clock cap.
///
/// Two traps this exists to avoid, both of which the diagnostics checkers hit when they
/// shelled out by hand:
///
///   1. <c>StandardOutput.ReadToEnd()</c> blocks until the child closes stdout, i.e. until
///      it exits. Calling it BEFORE <c>WaitForExit(timeout)</c> makes the timeout dead
///      code: the wait is only reached once the child is already gone. `PRAGMA quick_check`
///      on a multi-GB library.db over spinning storage then pins the HTTP request for
///      minutes with no cap at all.
///   2. Redirecting stderr and never draining it deadlocks any child that writes more than
///      the ~64 KB pipe buffer, forever.
///
/// So: start both reads first, wait with a real timeout, kill on overrun.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Starts <paramref name="fileName"/> and returns its output, or <c>null</c> if it did
    /// not exit within <paramref name="timeoutMs"/> (in which case the process tree is killed).
    /// Failures to start (missing binary, denied exec) throw, as the caller must decide.
    /// </summary>
    public static async Task<ProcessResult?> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        // Drain BOTH pipes concurrently, starting now - see the class remarks.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or the OS refused. Either way there is nothing left to do.
            }

            // Let the (now closing) pipes finish so no reader task is left dangling.
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The output of a killed process is worthless; it is being discarded anyway.
            }

            return null;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}
