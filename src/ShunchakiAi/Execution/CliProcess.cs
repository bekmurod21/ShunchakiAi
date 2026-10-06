using System.Diagnostics;
using System.Text;

namespace ShunchakiAi.Execution;

public sealed record CliResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Runs an agent CLI (Claude Code, Gemini CLI) as a child process: the prompt goes in on stdin,
/// stdout is streamed line by line (for live progress) and captured up to a fixed budget,
/// stderr is captured bounded, and the whole process tree is killed on timeout or Ctrl+C.
/// </summary>
public static class CliProcess
{
    private const int MaxStdoutChars = 8 * 1024 * 1024;

    public static async Task<CliResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        string? standardInput,
        IReadOnlyCollection<string> removeEnvironment,
        Action<string>? onStdoutLine,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var name in removeEnvironment)
        {
            info.Environment.Remove(name);
        }

        info.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = info };
        process.Start();

        var stdout = new StringBuilder();
        var stderr = new BoundedTextBuffer(8_000, 8_000);
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (stdout.Length < MaxStdoutChars)
                {
                    stdout.AppendLine(line);
                }

                onStdoutLine?.Invoke(line);
            }
        }, CancellationToken.None);
        var stderrTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                stderr.Append(buffer.AsSpan(0, read));
            }
        }, CancellationToken.None);

        // Write the prompt concurrently: the child may produce output before reading all of stdin.
        var stdinTask = Task.Run(async () =>
        {
            try
            {
                if (standardInput is not null)
                {
                    await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child exited without reading its input; its exit code tells the story.
            }
        }, CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, stdinTask).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Orphaned grandchildren still hold the pipes; use what was captured.
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CliResult(process.HasExited ? process.ExitCode : -1, stdout.ToString(), stderr.ToString(), timedOut);
    }

    /// <summary>Finds an executable on PATH (with .cmd/.exe on Windows), or uses an explicit override.</summary>
    public static string? Locate(string name, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
        }

        string[] suffixes = OperatingSystem.IsWindows() ? [".cmd", ".exe", ".bat", string.Empty] : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(directory, name + suffix);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
