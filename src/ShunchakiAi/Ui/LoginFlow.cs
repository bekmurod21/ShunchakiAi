using ShunchakiAi.Agent;
using ShunchakiAi.Api;
using System.Diagnostics;
using ShunchakiAi.Configuration;
using ShunchakiAi.Execution;

namespace ShunchakiAi.Ui;

/// <summary>
/// Entering, verifying, saving and removing API keys while the program runs: the first-run
/// setup and the /login, /logout and /keys commands.
/// </summary>
public sealed class LoginFlow(
    CredentialStore credentials, ProviderRegistry providers, Func<ModelRouter?> router, AgentOptions options, ConsoleRenderer ui)
{
    private const int MaxAttempts = 3;

    /// <summary>Registers a provider for every key found in the environment or the saved file.</summary>
    public void ApplyStoredCredentials()
    {
        foreach (var provider in CredentialStore.Providers)
        {
            if (credentials.Get(provider) is { } credential)
            {
                providers.Set(ProviderFactory.Create(provider, credential.Secret, options));
            }
        }
    }

    private static readonly string[] AllBackends =
        [ModelProviders.ClaudeCode, ModelProviders.GeminiCli, ModelProviders.Anthropic, ModelProviders.Gemini];

    /// <summary>
    /// Registers Claude Code and Gemini CLI when their executables are installed. Whether they are
    /// logged in is discovered on first use; a login error moves the work to the next model.
    /// </summary>
    public void RegisterInstalledClis()
    {
        foreach (var backend in new[] { ModelProviders.ClaudeCode, ModelProviders.GeminiCli })
        {
            if (Locate(backend) is { } path && !providers.Has(backend))
            {
                providers.Set(CreateCli(backend, path));
            }
        }
    }

    /// <summary>
    /// Log in to a backend (choosing it first when <paramref name="provider"/> is null): an API key
    /// for Anthropic/Gemini, or the official CLI's own login for the subscription backends.
    /// </summary>
    public async Task<bool> LoginAsync(string? provider, CancellationToken cancellationToken = default)
    {
        if (!ui.IsInteractive)
        {
            ui.ShowError("Cannot log in: the terminal is not interactive. Set ANTHROPIC_API_KEY or GEMINI_API_KEY, " +
                         "or log in to `claude` / `gemini` yourself first.");
            return false;
        }

        provider ??= ui.Choose("How do you want to use Claude or Gemini?",
            AllBackends.Select(p => (p, ModelProviders.DisplayName(p))).ToList());
        if (provider is null)
        {
            return false;
        }

        if (ModelProviders.IsSubscription(provider))
        {
            return await SubscriptionLoginAsync(provider);
        }

        ui.ShowInfo(provider == ModelProviders.Gemini
            ? "Get a Gemini API key at https://aistudio.google.com/apikey"
            : "Get a Claude API key at https://console.anthropic.com/settings/keys (an Anthropic OAuth access token, sk-ant-oat…, also works)");

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var secret = ui.ReadSecret($"{ModelProviders.DisplayName(provider)} key");
            if (string.IsNullOrWhiteSpace(secret))
            {
                ui.ShowInfo("Cancelled.");
                return false;
            }

            secret = secret.Trim();
            var candidate = ProviderFactory.Create(provider, secret, options);
            var check = await ui.ShowProgressAsync("Checking the key...", () => candidate.CheckKeyAsync(cancellationToken));

            if (check.Result == KeyCheckResult.Invalid)
            {
                candidate.Dispose();
                ui.ShowError($"{ModelProviders.DisplayName(provider)}: {check.Message}. Try again (or press Enter to cancel).");
                continue;
            }

            if (check.Result == KeyCheckResult.Unknown)
            {
                ui.ShowWarning($"{check.Message}. Using it anyway.");
            }

            var save = ui.Confirm($"Save this key for future sessions ({credentials.FilePath})?", defaultValue: true);
            credentials.Set(provider, secret, save);
            providers.Set(candidate);
            router()?.ProviderKeyChanged(provider);

            ui.ShowSuccess($"{ModelProviders.DisplayName(provider)} key {(check.Result == KeyCheckResult.Valid ? "verified and " : string.Empty)}" +
                           $"active{(save ? " (saved)" : " for this session only")}.");
            return true;
        }

        ui.ShowError("Too many invalid attempts.");
        return false;
    }

    /// <summary>
    /// Hands the terminal to the official CLI so the user logs in there (Claude Pro/Max via
    /// Claude Code's /login, Google account via Gemini CLI). Shunchaki never sees the credentials.
    /// </summary>
    private async Task<bool> SubscriptionLoginAsync(string backend)
    {
        var (command, package, steps) = backend == ModelProviders.ClaudeCode
            ? ("claude", "@anthropic-ai/claude-code", "type /login, choose your Claude Pro/Max account, then /exit")
            : ("gemini", "@google/gemini-cli", "choose \"Login with Google\", finish in the browser, then /quit");

        if (Locate(backend) is not { } path)
        {
            ui.ShowError($"`{command}` is not installed. Install it with:  npm install -g {package}");
            return false;
        }

        ui.ShowInfo($"Starting `{command}`: {steps}. You will return to Shunchaki afterwards.");
        try
        {
            var info = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = options.WorkingDirectory };
            foreach (var name in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "GEMINI_API_KEY", "GOOGLE_API_KEY" })
            {
                // Make the CLI use (and offer) its subscription login, not an API key.
                info.Environment.Remove(name);
            }

            using var process = Process.Start(info);
            if (process is not null)
            {
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ui.ShowError($"Could not start `{command}`: {ex.Message}");
            return false;
        }

        providers.Set(CreateCli(backend, path));
        router()?.ProviderKeyChanged(backend);
        ui.ShowSuccess($"{ModelProviders.DisplayName(backend)} is ready. Its usage comes from your subscription limits.");
        return true;
    }

    private static string? Locate(string backend) => backend == ModelProviders.ClaudeCode
        ? CliProcess.Locate("claude", Environment.GetEnvironmentVariable("SHUNCHAKI_CLAUDE_CODE_PATH"))
        : CliProcess.Locate("gemini", Environment.GetEnvironmentVariable("SHUNCHAKI_GEMINI_CLI_PATH"));

    private IModelProvider CreateCli(string backend, string path) => backend == ModelProviders.ClaudeCode
        ? new ClaudeCodeProvider(path, options)
        : new GeminiCliProvider(path, options);

    public void Logout(string? provider)
    {
        if (provider is not null && ModelProviders.IsSubscription(provider))
        {
            ui.ShowInfo($"Log out inside the CLI itself (`{(provider == ModelProviders.ClaudeCode ? "claude" : "gemini")}`), " +
                        "or remove it from the chain with -m.");
            return;
        }

        var targets = provider is null ? CredentialStore.Providers : [provider];
        foreach (var target in targets)
        {
            var credential = credentials.Get(target);
            credentials.Remove(target);
            providers.Remove(target);
            if (credential?.Source == CredentialSource.Environment)
            {
                ui.ShowWarning($"{ModelProviders.DisplayName(target)} key came from an environment variable; " +
                               "it is disabled for this session but will be used again next time unless you unset it.");
            }
            else if (credential is not null)
            {
                ui.ShowInfo($"{ModelProviders.DisplayName(target)} key removed.");
            }
        }
    }

    public void ShowKeys()
    {
        foreach (var backend in new[] { ModelProviders.ClaudeCode, ModelProviders.GeminiCli })
        {
            var state = providers.Has(backend)
                ? $"installed: {Locate(backend)} (login is managed by the CLI; /login {(backend == ModelProviders.ClaudeCode ? "claude-code" : "gemini-cli")})"
                : "not installed";
            ui.ShowInfo($"{ModelProviders.DisplayName(backend),-36} {state}");
        }

        foreach (var provider in CredentialStore.Providers)
        {
            var credential = credentials.Get(provider);
            var active = providers.Has(provider);
            var state = credential is null || !active
                ? "not set (use /login)"
                : $"{credential.Masked}  [{credential.Source switch
                {
                    CredentialSource.Environment => "environment variable",
                    CredentialSource.SavedFile => "saved",
                    _ => "this session only",
                }}]";
            ui.ShowInfo($"{ModelProviders.DisplayName(provider),-36} {state}");
        }

        ui.ShowInfo($"Saved keys file: {credentials.FilePath}");
    }

    /// <summary>Maps "claude", "anthropic", "gemini", "google" to a provider name.</summary>
    public static string? ParseProvider(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "claude" or "anthropic" => ModelProviders.Anthropic,
        "gemini" or "google" => ModelProviders.Gemini,
        "claude-code" or "claudecode" => ModelProviders.ClaudeCode,
        "gemini-cli" or "geminicli" => ModelProviders.GeminiCli,
        _ => throw new ArgumentException($"Unknown provider '{text}'. Use claude-code, gemini-cli, claude or gemini."),
    };
}
