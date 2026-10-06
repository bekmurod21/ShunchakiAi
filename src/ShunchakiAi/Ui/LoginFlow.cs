using ShunchakiAi.Agent;
using ShunchakiAi.Api;
using ShunchakiAi.Configuration;

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

    /// <summary>Asks for a key (choosing the provider first when <paramref name="provider"/> is null).</summary>
    public async Task<bool> LoginAsync(string? provider, CancellationToken cancellationToken = default)
    {
        if (!ui.IsInteractive)
        {
            ui.ShowError("Cannot ask for a key: the terminal is not interactive. Set ANTHROPIC_API_KEY or GEMINI_API_KEY instead.");
            return false;
        }

        provider ??= ui.Choose("Which provider's key do you want to enter?",
            CredentialStore.Providers.Select(p => (p, ModelProviders.DisplayName(p))).ToList());
        if (provider is null)
        {
            return false;
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

    public void Logout(string? provider)
    {
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
            ui.ShowInfo($"{ModelProviders.DisplayName(provider),-20} {state}");
        }

        ui.ShowInfo($"Saved keys file: {credentials.FilePath}");
    }

    /// <summary>Maps "claude", "anthropic", "gemini", "google" to a provider name.</summary>
    public static string? ParseProvider(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "claude" or "anthropic" => ModelProviders.Anthropic,
        "gemini" or "google" => ModelProviders.Gemini,
        _ => throw new ArgumentException($"Unknown provider '{text}'. Use 'claude' or 'gemini'."),
    };
}
