namespace ShunchakiAi.Configuration;

/// <summary>
/// Immutable runtime configuration, resolved once at startup from CLI flags and
/// environment variables. The API key is only ever read from the environment.
/// </summary>
public sealed record AgentOptions
{
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";
    public const string DefaultModel = "claude-opus-5-5";

    private static readonly string[] EffortLevels = ["low", "medium", "high", "xhigh", "max"];

    public required string ApiKey { get; init; }
    public required Uri BaseUrl { get; init; }
    public required string Model { get; init; }
    public required string Effort { get; init; }
    public required int MaxTokens { get; init; }
    public required int MaxToolIterations { get; init; }
    public required TimeSpan ShellTimeout { get; init; }
    public required string WorkingDirectory { get; init; }
    public required bool AutoApprove { get; init; }
    public required bool UseServerFallback { get; init; }

    public static AgentOptions Load(CliArguments cli)
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ConfigurationException(
                $"{ApiKeyVariable} is not set. Export it first, e.g.  export {ApiKeyVariable}=sk-ant-...");
        }

        var model = cli.Model ?? Env("SHUNCHAKI_MODEL") ?? DefaultModel;
        var effort = (cli.Effort ?? Env("SHUNCHAKI_EFFORT") ?? "high").ToLowerInvariant();
        if (!EffortLevels.Contains(effort))
        {
            throw new ConfigurationException($"Invalid effort '{effort}'. Expected one of: {string.Join(", ", EffortLevels)}.");
        }

        var baseUrl = Env("ANTHROPIC_BASE_URL") ?? "https://api.anthropic.com/";
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ConfigurationException($"ANTHROPIC_BASE_URL '{baseUrl}' is not a valid absolute URL.");
        }

        var workingDirectory = Path.GetFullPath(cli.WorkingDirectory ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(workingDirectory))
        {
            throw new ConfigurationException($"Working directory '{workingDirectory}' does not exist.");
        }

        return new AgentOptions
        {
            ApiKey = apiKey.Trim(),
            BaseUrl = baseUri,
            Model = model,
            Effort = effort,
            MaxTokens = IntEnv("SHUNCHAKI_MAX_TOKENS", 16_000),
            MaxToolIterations = IntEnv("SHUNCHAKI_MAX_TOOL_ITERATIONS", 50),
            ShellTimeout = TimeSpan.FromSeconds(IntEnv("SHUNCHAKI_SHELL_TIMEOUT", 120)),
            WorkingDirectory = workingDirectory,
            AutoApprove = cli.AutoApprove,
            UseServerFallback = Env("SHUNCHAKI_FALLBACK") is not ("0" or "off" or "false")
                && ModelCapabilities.SupportsServerFallback(model),
        };
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
