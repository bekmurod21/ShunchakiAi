using System.Reflection;
using System.Text;
using ShunchakiAi;
using ShunchakiAi.Agent;
using ShunchakiAi.Api;
using ShunchakiAi.Configuration;
using ShunchakiAi.Execution;
using ShunchakiAi.Tools;
using ShunchakiAi.Ui;

Console.OutputEncoding = Encoding.UTF8;
ConsoleRenderer.ConfigureConsole();
var ui = new ConsoleRenderer();

CliArguments cli;
AgentOptions options;
try
{
    cli = CliArguments.Parse(args);
    if (cli.ShowHelp)
    {
        ConsoleRenderer.ShowHelp();
        return 0;
    }

    if (cli.ShowVersion)
    {
        Console.WriteLine($"ai {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}");
        return 0;
    }

    options = AgentOptions.Load(cli);
}
catch (ConfigurationException ex)
{
    ui.ShowError(ex.Message);
    return 2;
}

// Piped input (e.g. `git diff | ai "review this"`) is attached to the prompt.
var prompt = cli.Prompt;
if (Console.IsInputRedirected)
{
    var piped = (await StandardInput.ReadAsync(waitForFirstData: prompt is null)).Trim();
    if (piped.Length > 0)
    {
        prompt = prompt is null ? piped : $"{prompt}\n\n<stdin>\n{piped}\n</stdin>";
    }
}

using var client = new AnthropicClient(options.BaseUrl, options.ApiKey, options.UseServerFallback);
var files = new FileSystemService(options.WorkingDirectory);
var tools = ToolRegistry.CreateDefault(files, new ShellExecutor(), options.ShellTimeout);
var session = new AgentSession(client, tools, ui, options);
using var cancellation = new TurnCancellation();

// Single-shot mode: run one request and exit with a status code.
if (prompt is not null)
{
    var token = cancellation.BeginTurn();
    var ok = await session.RunTurnAsync(prompt, token);
    cancellation.EndTurn();
    return ok ? 0 : 1;
}

if (Console.IsInputRedirected)
{
    ui.ShowError("No prompt given. Pass one as an argument or pipe it on stdin.");
    return 2;
}

ui.ShowBanner(options);
return await new Repl(session, ui, cancellation).RunAsync();
