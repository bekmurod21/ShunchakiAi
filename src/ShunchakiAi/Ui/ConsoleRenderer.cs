using System.Text;
using ShunchakiAi.Agent;
using ShunchakiAi.Api;
using ShunchakiAi.Configuration;
using ShunchakiAi.Tools;
using Spectre.Console;

namespace ShunchakiAi.Ui;

/// <summary>All terminal input/output. No business logic lives here.</summary>
public sealed class ConsoleRenderer : IAgentView
{
    private const int ToolPreviewLines = 12;
    private readonly IAnsiConsole _console = AnsiConsole.Console;

    /// <summary>
    /// When stdout is redirected (pipes, CI logs) Spectre reports a width of -1 and renders
    /// nothing at all; pin a sensible width so piped output still works.
    /// </summary>
    public static void ConfigureConsole()
    {
        if (AnsiConsole.Profile.Width <= 0)
        {
            AnsiConsole.Profile.Width =
                int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var columns) && columns > 0 ? columns : 120;
        }
    }

    public bool IsInteractive => _console.Profile.Capabilities.Interactive && !Console.IsInputRedirected;

    public void ShowBanner(AgentOptions options)
    {
        _console.Write(new FigletText("Shunchaki AI").Color(Color.DeepSkyBlue1));
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[grey]model[/]", Markup.Escape($"{options.Model} (effort: {options.Effort})"));
        grid.AddRow("[grey]workspace[/]", Markup.Escape(options.WorkingDirectory));
        grid.AddRow("[grey]approvals[/]", options.AutoApprove ? "[yellow]auto (--yes)[/]" : "ask before writes and commands");
        _console.Write(grid);
        _console.MarkupLine("[grey]Type a request, or /help for commands. Ctrl+C interrupts a running turn.[/]");
        _console.WriteLine();
    }

    public static void ShowHelp()
    {
        AnsiConsole.MarkupLine("""
            [bold deepskyblue1]Shunchaki AI[/] - an AI coding agent for your terminal

            [bold]Usage[/]
              ai                          Start an interactive session (REPL)
              ai [[options]] "<prompt>"     Run a single request and exit
              cat log.txt | ai "explain"   Piped stdin is appended to the prompt

            [bold]Options[/]
              -m, --model <id>            Model (default: claude-opus-5-5, env SHUNCHAKI_MODEL)
              -e, --effort <level>        low | medium | high | xhigh | max (default: high)
              -C, --cwd <dir>             Workspace root (default: current directory)
              -y, --yes                   Auto-approve file writes and shell commands
              -v, --version               Print version
              -h, --help                  Show this help

            [bold]Environment[/]
              ANTHROPIC_API_KEY           Required. Your Anthropic API key
              ANTHROPIC_BASE_URL          Optional API endpoint override
              SHUNCHAKI_MAX_TOKENS        Max output tokens per response (default 16000)
              SHUNCHAKI_SHELL_TIMEOUT     Default shell command timeout in seconds (default 120)
              SHUNCHAKI_FALLBACK=off      Disable server-side refusal fallback

            [bold]REPL commands[/]
              /help  /clear  /usage  /exit
            """);
    }

    public void ShowReplHelp() => _console.MarkupLine("""
        [bold]/clear[/]  start a new conversation
        [bold]/usage[/]  show token usage for this session
        [bold]/exit[/]   quit (also /quit, Ctrl+D)
        End a line with [bold]\[/] to continue typing on the next line.
        """);

    /// <summary>Reads one (possibly multi-line) request. Returns null on EOF.</summary>
    public string? ReadUserInput()
    {
        var builder = new StringBuilder();
        _console.Markup("[bold deepskyblue1]›[/] ");
        while (true)
        {
            var line = Console.ReadLine();
            if (line is null)
            {
                return builder.Length > 0 ? builder.ToString() : null;
            }

            if (line.EndsWith('\\'))
            {
                builder.Append(line, 0, line.Length - 1).Append('\n');
                _console.Markup("[grey]…[/] ");
                continue;
            }

            builder.Append(line);
            return builder.ToString();
        }
    }

    public async Task<T> ShowProgressAsync<T>(string status, Func<Task<T>> work)
    {
        if (!_console.Profile.Capabilities.Interactive)
        {
            return await work().ConfigureAwait(false);
        }

        return await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.DeepSkyBlue1))
            .StartAsync($"[grey]{Markup.Escape(status)}[/]", _ => work())
            .ConfigureAwait(false);
    }

    public void ShowAssistantText(string markdown)
    {
        foreach (var renderable in MarkdownRenderer.Render(markdown))
        {
            _console.Write(renderable);
        }
    }

    public void ShowThinking(string summary) =>
        _console.MarkupLine($"[grey italic]✻ {Markup.Escape(summary.Trim())}[/]");

    public void ShowToolCall(ITool tool, string summary)
    {
        var color = tool.RequiresApproval ? "yellow" : "deepskyblue1";
        _console.MarkupLine($"[{color}]⏺[/] [bold]{Markup.Escape(tool.Name)}[/] [grey]{Markup.Escape(Truncate(summary, 200))}[/]");
    }

    public void ShowToolResult(ToolResult result)
    {
        var lines = result.Content.TrimEnd().Split('\n');
        var preview = string.Join('\n', lines.Take(ToolPreviewLines));
        if (lines.Length > ToolPreviewLines)
        {
            preview += $"\n… {lines.Length - ToolPreviewLines} more lines";
        }

        var color = result.IsError ? "red" : "grey";
        foreach (var line in preview.Split('\n'))
        {
            _console.MarkupLine($"  [{color}]⎿ {Markup.Escape(Truncate(line, 160))}[/]");
        }
    }

    public Task<bool> ApproveAsync(ITool tool, string summary, CancellationToken cancellationToken)
    {
        if (!IsInteractive)
        {
            ShowWarning($"Skipped {tool.Name}: approval needed but the terminal is not interactive (use --yes).");
            return Task.FromResult(false);
        }

        var panel = new Panel(new Text(summary))
            .Header($"[yellow] {Markup.Escape(tool.Name)} wants to run [/]")
            .BorderStyle(new Style(Color.Yellow))
            .Border(BoxBorder.Rounded);
        _console.Write(panel);

        var approved = _console.Prompt(new ConfirmationPrompt("[yellow]Allow?[/]") { DefaultValue = true });
        return Task.FromResult(approved);
    }

    public void ShowWarning(string message) => _console.MarkupLine($"[yellow]⚠ {Markup.Escape(message)}[/]");

    public void ShowError(string message) => _console.MarkupLine($"[red]✖ {Markup.Escape(message)}[/]");

    public void ShowInfo(string message) => _console.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    public void ShowUsage(TokenUsage turn, TokenUsage total) =>
        _console.MarkupLine(
            $"[grey dim]tokens: {turn.InputTokens + turn.CacheReadTokens + turn.CacheWriteTokens:N0} in " +
            $"({turn.CacheReadTokens:N0} cached) · {turn.OutputTokens:N0} out · session {total.OutputTokens:N0} out[/]\n");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
