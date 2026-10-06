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

// Piped input (e.g. `git diff | shunchaki "review this"`) is attached to the prompt.
var prompt = cli.Prompt;
if (Console.IsInputRedirected)
{
    var piped = (await StandardInput.ReadAsync(waitForFirstData: prompt is null)).Trim();
    if (piped.Length > 0)
    {
        prompt = prompt is null ? piped : $"{prompt}\n\n<stdin>\n{piped}\n</stdin>";
    }
}

// API keys: environment variables first, then keys saved earlier with /login. If none are
// found and the terminal is interactive, ask for one now instead of failing.
var credentials = new CredentialStore();
using var providers = new ProviderRegistry();
AgentSession? session = null;
var login = new LoginFlow(credentials, providers, () => session?.Router, options, ui);
login.ApplyStoredCredentials();

if (providers.IsEmpty)
{
    if (!ui.IsInteractive)
    {
        ui.ShowError("No API key found. Set ANTHROPIC_API_KEY (Claude) or GEMINI_API_KEY (Gemini), " +
                     "or run shunchaki interactively once to enter and save a key.");
        return 2;
    }

    ui.ShowInfo("Welcome to Shunchaki AI! No API key is configured yet - let's add one.");
    if (!await login.LoginAsync(null))
    {
        ui.ShowError("Shunchaki needs a Claude or Gemini key to work. Run it again when you have one.");
        return 2;
    }
}

var files = new FileSystemService(options.WorkingDirectory);
var store = new SessionStore(options.WorkingDirectory);
var workLog = new WorkLog();
var tools = ToolRegistry.CreateDefault(files, new ShellExecutor(), options.ShellTimeout, workLog, () => session?.ActiveModel);
session = new AgentSession(providers, tools, ui, options, new ModelRouter(options.Models, providers.IsAvailable), workLog, store);
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

ui.ShowBanner(options, session.SessionPath, providers.Has);
return await new Repl(session, store, login, ui, cancellation).RunAsync();
