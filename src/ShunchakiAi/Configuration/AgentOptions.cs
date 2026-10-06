using ShunchakiAi.Api;

namespace ShunchakiAi.Configuration;

/// <summary>
/// Immutable runtime configuration, resolved once at startup from CLI flags and
/// environment variables. API keys are only ever read from the environment.
/// </summary>
public sealed record AgentOptions
{
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>Claude part of the default failover chain.</summary>
    public static readonly string[] DefaultClaudeChain = [DefaultModel, "claude-sonnet-5-5", "claude-haiku-4-5"];

    /// <summary>Gemini part of the default chain (override with SHUNCHAKI_GEMINI_MODELS).</summary>
    public static readonly string[] DefaultGeminiChain = ["gemini-3.1-pro-preview", "gemini-3.8-flash"];

    private static readonly string[] EffortLevels = ["low", "medium", "high", "xhigh", "max"];

    public required Uri AnthropicBaseUrl { get; init; }
    public required Uri GeminiBaseUrl { get; init; }

    /// <summary>
    /// Failover chain, most preferred first. Models whose provider has no API key yet are
    /// skipped at runtime until a key is added (environment, saved file or /login).
    /// </summary>
    public required IReadOnlyList<string> Models { get; init; }
    public string Model => Models[0];
    public required string Effort { get; init; }
    public required int MaxTokens { get; init; }
    public required int MaxToolIterations { get; init; }
    public required int MaxAutoContinue { get; init; }
    public required TimeSpan MaxWait { get; init; }
    public required bool Resume { get; init; }
    public string? SessionId { get; init; }
    public required TimeSpan ShellTimeout { get; init; }
    public required string WorkingDirectory { get; init; }
    public required bool AutoApprove { get; init; }
    public required bool UseServerFallback { get; init; }

    public static AgentOptions Load(CliArguments cli)
    {
        var models = ParseModels(cli.Model ?? Env("SHUNCHAKI_MODELS") ?? Env("SHUNCHAKI_MODEL"));
        if (cli.NoFailover)
        {
            models = [models[0]];
        }

        var effort = (cli.Effort ?? Env("SHUNCHAKI_EFFORT") ?? "high").ToLowerInvariant();
        if (!EffortLevels.Contains(effort))
        {
            throw new ConfigurationException($"Invalid effort '{effort}'. Expected one of: {string.Join(", ", EffortLevels)}.");
        }

        var workingDirectory = Path.GetFullPath(cli.WorkingDirectory ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(workingDirectory))
        {
            throw new ConfigurationException($"Working directory '{workingDirectory}' does not exist.");
        }

        return new AgentOptions
        {
            AnthropicBaseUrl = BaseUrl("ANTHROPIC_BASE_URL", "https://api.anthropic.com/"),
            GeminiBaseUrl = BaseUrl("GEMINI_BASE_URL", "https://generativelanguage.googleapis.com/"),
            Models = models,
            Effort = effort,
            MaxTokens = IntEnv("SHUNCHAKI_MAX_TOKENS", 16_000),
            MaxToolIterations = IntEnv("SHUNCHAKI_MAX_TOOL_ITERATIONS", 100),
            MaxAutoContinue = IntEnv("SHUNCHAKI_MAX_CONTINUE", 5),
            MaxWait = TimeSpan.FromSeconds(IntEnv("SHUNCHAKI_MAX_WAIT", 600)),
            Resume = cli.Resume,
            SessionId = cli.SessionId,
            ShellTimeout = TimeSpan.FromSeconds(IntEnv("SHUNCHAKI_SHELL_TIMEOUT", 120)),
            WorkingDirectory = workingDirectory,
            AutoApprove = cli.AutoApprove,
            UseServerFallback = Env("SHUNCHAKI_FALLBACK") is not ("0" or "off" or "false"),
        };
    }

    /// <summary>
    /// Default: the Claude chain followed by the Gemini chain.
    /// A single model gets the default chain appended as fallbacks; an explicit comma-separated
    /// list is used exactly as given.
    /// </summary>
    private static string[] ParseModels(string? value)
    {
        var geminiChain = Env("SHUNCHAKI_GEMINI_MODELS")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? DefaultGeminiChain;
        string[] defaults = [.. DefaultClaudeChain, .. geminiChain];

        if (value is null)
        {
            return defaults;
        }

        var models = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (models.Length == 0)
        {
            throw new ConfigurationException("No model given.");
        }

        return models.Length > 1
            ? models
            : [models[0], .. defaults.Where(m => m != models[0])];
    }

    private static Uri BaseUrl(string variable, string fallback)
    {
        var url = Env(variable) ?? fallback;
        if (!url.EndsWith('/'))
        {
            url += "/";
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : throw new ConfigurationException($"{variable} '{url}' is not a valid absolute URL.");
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int IntEnv(string name, int fallback) =>
        int.TryParse(Env(name), out var value) && value > 0 ? value : fallback;
}

/// <summary>Request-shape differences between model families.</summary>
public static class ModelCapabilities
{
    private static readonly string[] FallbackModels =
        ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"];

    /// <summary>Haiku 4.5 still uses budget-based thinking and has no effort control.</summary>
    public static bool SupportsAdaptiveThinking(string model) => !model.StartsWith("claude-haiku", StringComparison.Ordinal);

    public static bool SupportsServerFallback(string model) => FallbackModels.Contains(model);
}
