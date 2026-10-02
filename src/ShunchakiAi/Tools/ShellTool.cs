using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ShunchakiAi.Execution;

namespace ShunchakiAi.Tools;

public sealed partial class RunShellTool(ShellExecutor executor, FileSystemService files, TimeSpan defaultTimeout) : ITool
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    public string Name => "run_shell";
    public string Description =>
        $"Run a non-interactive shell command in the workspace root using {(OperatingSystem.IsWindows() ? "cmd.exe" : "bash")}. " +
        "stdin is closed, so commands must not wait for input. Returns exit code, stdout and stderr (long output is truncated).";
    public bool RequiresApproval => true;

    public JsonObject CreateInputSchema() => ToolInput.Schema(
        ("command", "string", "The command line to execute.", true),
        ("timeout_seconds", "integer", $"Optional timeout. Default {defaultTimeout.TotalSeconds:0}, max {MaxTimeout.TotalSeconds:0}.", false));

    public string Describe(JsonObject input) => $"$ {ToolInput.OptionalString(input, "command")}";

    public async Task<ToolResult> ExecuteAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var command = ToolInput.RequireString(input, "command").Trim();
        if (command.Length == 0)
        {
            throw new ToolInputException("command must not be empty.");
        }

        if (IsCatastrophic(command))
        {
            return ToolResult.Error("Refused: this command is blocked because it could destroy the system or the user's data.");
        }

        var seconds = ToolInput.OptionalInt(input, "timeout_seconds", (int)defaultTimeout.TotalSeconds);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxTimeout.TotalSeconds));

        var result = await executor.RunAsync(command, files.Root, timeout, cancellationToken).ConfigureAwait(false);

        var output = new StringBuilder();
        output.AppendLine(result.TimedOut
            ? $"Command timed out after {timeout.TotalSeconds:0}s and was killed."
            : $"Exit code: {result.ExitCode} ({result.Duration.TotalSeconds:0.0}s)");

        if (result.StandardOutput.Length > 0)
        {
            output.AppendLine("<stdout>").AppendLine(result.StandardOutput.TrimEnd()).AppendLine("</stdout>");
        }

        if (result.StandardError.Length > 0)
        {
            output.AppendLine("<stderr>").AppendLine(result.StandardError.TrimEnd()).AppendLine("</stderr>");
        }

        return new ToolResult(output.ToString(), IsError: result.TimedOut || result.ExitCode != 0);
    }

    /// <summary>
    /// Last-line-of-defence deny list, applied even with --yes. The primary safeguard is
    /// the interactive approval prompt; this only catches the unambiguous disasters.
    /// </summary>
    private static bool IsCatastrophic(string command) => CatastrophicPattern().IsMatch(command);

    [GeneratedRegex(
        @"\brm\s+(-[a-zA-Z]*\s+)*-[a-zA-Z]*[rR][a-zA-Z]*\s+(-[a-zA-Z]*\s+)*(/|~|\$HOME|/\*)(\s|$)" +
        @"|:\(\)\s*\{\s*:\|:&\s*\};:" +
        @"|\bmkfs(\.\w+)?\b" +
        @"|\bdd\s+.*\bof=/dev/(sd|nvme|hd|disk)" +
        @"|>\s*/dev/(sd|nvme|hd|disk)" +
        @"|\b(shutdown|reboot|halt|poweroff)\b" +
        @"|\bformat\s+[a-zA-Z]:" +
        @"|\b(rd|rmdir)\s+/s\s+(/q\s+)?[a-zA-Z]:\\?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CatastrophicPattern();
}
