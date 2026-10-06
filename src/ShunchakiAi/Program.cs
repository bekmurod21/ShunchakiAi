using System.Reflection;
using System.Text;
using ShunchakiAi;
using ShunchakiAi.Agent;
using ShunchakiAi.Api;
using ShunchakiAi.Configuration;
using ShunchakiAi.Execution;
using ShunchakiAi.Sessions;
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
        Console.WriteLine($"shunchaki {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}");
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

var providerList = new List<IModelProvider>();
if (options.AnthropicApiKey is { } anthropicKey)
{
    providerList.Add(new AnthropicClient(options.AnthropicBaseUrl, anthropicKey));
}

if (options.GeminiApiKey is { } geminiKey)
{
    providerList.Add(new GeminiClient(options.GeminiBaseUrl, geminiKey));
}

using var providers = new ProviderRegistry(providerList);
foreach (var skipped in options.SkippedModels)
{
    ui.ShowWarning($"Skipping {skipped}: no API key for {ModelProviders.ProviderOf(skipped)}.");
}

var files = new FileSystemService(options.WorkingDirectory);
var store = new SessionStore(options.WorkingDirectory);
var workLog = new WorkLog();
AgentSession? session = null;
var tools = ToolRegistry.CreateDefault(files, new ShellExecutor(), options.ShellTimeout, workLog, () => session?.ActiveModel);
session = new AgentSession(providers, tools, ui, options, new ModelRouter(options.Models), workLog, store);
using var cancellation = new TurnCancellation();

if (options.Resume)
{
    var id = options.SessionId ?? store.LatestId();
    if (id is null)
    {
        ui.ShowError($"No saved session found in {store.Directory}.");
        return 2;
    }

    if (!SessionLoader.TryResume(session, store, ui, id))
    {
        return 2;
    }
}

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

ui.ShowBanner(options, session.SessionPath);
return await new Repl(session, store, ui, cancellation).RunAsync();
