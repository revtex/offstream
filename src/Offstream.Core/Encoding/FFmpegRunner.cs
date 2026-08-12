using System.Diagnostics;

namespace Offstream.Core.Encoding;

/// <summary>ffmpeg exited non-zero, or could not be run at all.</summary>
public sealed class FFmpegException : Exception
{
    public FFmpegException()
    {
    }

    public FFmpegException(string message) : base(message)
    {
    }

    public FFmpegException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public FFmpegException(string message, int exitCode, string diagnostics) : base(message)
    {
        ExitCode = exitCode;
        Diagnostics = diagnostics;
    }

    public int ExitCode { get; }

    /// <summary>What ffmpeg wrote to stderr, which is where it explains itself.</summary>
    public string Diagnostics { get; } = string.Empty;
}

/// <summary>The outcome of one ffmpeg invocation.</summary>
/// <param name="ExitCode">ffmpeg's exit code; zero means success.</param>
/// <param name="StandardError">
/// stderr, where ffmpeg writes progress and every diagnostic worth reading.
/// </param>
/// <param name="StandardOutput">
/// stdout, which is empty for an encode but carries the answer for query invocations
/// such as <c>-version</c>.
/// </param>
public sealed record FFmpegResult(int ExitCode, string StandardError, string StandardOutput = "")
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs ffmpeg as a child process.
/// </summary>
/// <remarks>
/// <para>
/// Three rules are load-bearing here, all of them failure modes discovered the hard way:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>stderr is drained before waiting for exit.</b> ffmpeg writes progress to stderr
///     continuously. If nothing reads it, the pipe buffer fills, ffmpeg blocks writing, and
///     the parent blocks waiting — a deadlock that appears only on longer recordings, because
///     short ones never fill the buffer.
///   </item>
///   <item>
///     <b>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>.</b> Never a
///     command string; see <see cref="FFmpegArguments"/>.
///   </item>
///   <item>
///     <b>Every run has a deadline.</b> A wedged encoder must not hold a recording session
///     open forever; on cancellation the process tree is killed rather than orphaned.
///   </item>
/// </list>
/// </remarks>
public sealed class FFmpegRunner(string executablePath)
{
    /// <summary>Path to the ffmpeg executable this runner invokes.</summary>
    public string ExecutablePath { get; } = executablePath;

    /// <summary>Runs ffmpeg and returns its exit code and stderr.</summary>
    public async Task<FFmpegResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
                throw new FFmpegException($"Could not start ffmpeg at '{ExecutablePath}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new FFmpegException($"Could not start ffmpeg at '{ExecutablePath}'.", ex);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        // Start draining both pipes *before* awaiting exit. This ordering is the whole point.
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token);

            return new FFmpegResult(process.ExitCode, await stderr, await stdout);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);

            if (cancellationToken.IsCancellationRequested) throw;

            throw new FFmpegException(
                $"ffmpeg did not finish within {timeout.TotalSeconds:F0}s and was terminated.");
        }
    }

    /// <summary>Runs ffmpeg and throws unless it exits zero.</summary>
    /// <exception cref="FFmpegException">ffmpeg failed; the message carries its stderr.</exception>
    public async Task RunOrThrowAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(arguments, timeout, cancellationToken);
        if (result.Succeeded) return;

        throw new FFmpegException(
            $"ffmpeg exited with code {result.ExitCode}.{Environment.NewLine}{Tail(result.StandardError)}",
            result.ExitCode,
            result.StandardError);
    }

    /// <summary>ffmpeg's useful diagnostics are at the end; the rest is progress noise.</summary>
    private static string Tail(string text, int lines = 15)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, all.TakeLast(lines).Select(l => l.TrimEnd('\r')));
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exited between the check and the kill.
        }
    }
}
