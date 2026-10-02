using System.Diagnostics;

namespace ShunchakiAi.Execution;

public sealed record ShellResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, TimeSpan Duration);

/// <summary>
/// Runs a single shell command (bash on Unix, cmd on Windows) with no stdin, a hard timeout,
/// bounded stdout/stderr capture and guaranteed cleanup of the whole process tree.
/// </summary>
public sealed class ShellExecutor
{
    private const int HeadChars = 16_000;
    private const int TailChars = 16_000;
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    public async Task<ShellResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = CreateStartInfo(command, workingDirectory) };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the shell process.");
        }

        // No interactive input: a command waiting on a prompt sees EOF instead of hanging forever.
        process.StandardInput.Close();

        // Both pipes are drained concurrently; reading only one could deadlock when the
        // other pipe's OS buffer fills up.
        var stdout = new BoundedTextBuffer(HeadChars, TailChars);
        var stderr = new BoundedTextBuffer(HeadChars / 2, TailChars / 2);
        var stdoutTask = DrainAsync(process.StandardOutput, stdout);
        var stderrTask = DrainAsync(process.StandardError, stderr);

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
            KillTree(process);
        }

        try
        {
            // Killing the tree closes the pipes, which completes the readers. The grace
            // period guards against orphaned grandchildren still holding the handles.
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(DrainGrace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stderr.Append("\n[output streams did not close; some output may be missing]");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new ShellResult(
            process.HasExited ? process.ExitCode : -1,
            stdout.ToString(),
            stderr.ToString(),
            timedOut,
            stopwatch.Elapsed);
    }

    private static ProcessStartInfo CreateStartInfo(string command, string workingDirectory)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            // /d: skip AutoRun, /s + outer quotes: pass the command line through verbatim.
            info = new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{command}\"" };
        }
        else
        {
            info = new ProcessStartInfo(File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
        }

        info.WorkingDirectory = workingDirectory;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.Environment["TERM"] = "dumb";
        info.Environment["NO_COLOR"] = "1";
        info.Environment["GIT_PAGER"] = "cat";
        info.Environment["PAGER"] = "cat";
        return info;
    }

    private static async Task DrainAsync(StreamReader reader, BoundedTextBuffer sink)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            sink.Append(buffer.AsSpan(0, read));
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied or already terminating; nothing more we can do.
        }
    }
}
