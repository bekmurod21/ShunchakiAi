using System.Text.Json;
using System.Text.Json.Nodes;
using ShunchakiAi.Api;

namespace ShunchakiAi.Configuration;

public enum CredentialSource
{
    None,
    Environment,
    SavedFile,
    EnteredThisSession,
}

public sealed record Credential(string Provider, string Secret, CredentialSource Source)
{
    /// <summary>Safe to display: first and last characters only.</summary>
    public string Masked => Secret.Length <= 12 ? new string('•', Secret.Length) : $"{Secret[..7]}…{Secret[^4..]}";
}

/// <summary>
/// API keys for each provider. Resolution order: environment variables, then keys saved with
/// <c>/login</c> in the per-user credentials file. Keys entered at runtime can be saved there
/// (owner-only permissions on Unix) so later sessions start without asking again.
/// </summary>
public sealed class CredentialStore
{
    private static readonly Dictionary<string, string[]> EnvironmentVariables = new(StringComparer.Ordinal)
    {
        [ModelProviders.Anthropic] = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"],
        [ModelProviders.Gemini] = ["GEMINI_API_KEY", "GOOGLE_API_KEY"],
    };

    private readonly Dictionary<string, Credential> _credentials = new(StringComparer.Ordinal);

    public CredentialStore(string? filePath = null)
    {
        FilePath = filePath ?? DefaultPath();
        Load();
    }

    public string FilePath { get; }

    public static IReadOnlyList<string> Providers => [ModelProviders.Anthropic, ModelProviders.Gemini];

    public Credential? Get(string provider) => _credentials.GetValueOrDefault(provider);

    public bool HasAny => _credentials.Count > 0;

    /// <summary>Uses <paramref name="secret"/> for this run, and persists it when <paramref name="save"/>.</summary>
    public void Set(string provider, string secret, bool save)
    {
        _credentials[provider] = new Credential(provider, secret, save ? CredentialSource.SavedFile : CredentialSource.EnteredThisSession);
        if (save)
        {
            var saved = ReadFile();
            saved[provider] = secret;
            WriteFile(saved);
        }
    }

    /// <summary>Forgets the key for this run and deletes it from the credentials file.</summary>
    public void Remove(string provider)
    {
        _credentials.Remove(provider);
        var saved = ReadFile();
        if (saved.Remove(provider))
        {
            WriteFile(saved);
        }
    }

    private void Load()
    {
        var saved = ReadFile();
        foreach (var provider in Providers)
        {
            var fromEnvironment = EnvironmentVariables[provider]
                .Select(Environment.GetEnvironmentVariable)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            if (fromEnvironment is not null)
            {
                _credentials[provider] = new Credential(provider, fromEnvironment.Trim(), CredentialSource.Environment);
            }
            else if (saved.TryGetValue(provider, out var secret) && secret.Length > 0)
            {
                _credentials[provider] = new Credential(provider, secret, CredentialSource.SavedFile);
            }
        }
    }

    private Dictionary<string, string> ReadFile()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject root)
            {
                foreach (var (provider, value) in root)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var secret))
                    {
                        result[provider] = secret;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable credentials file behaves like an empty one; the user can /login again.
        }

        return result;
    }

    private void WriteFile(Dictionary<string, string> secrets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var root = new JsonObject();
        foreach (var (provider, secret) in secrets)
        {
            root[provider] = secret;
        }

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString());
        if (!OperatingSystem.IsWindows())
        {
            // Owner read/write only: the file holds secrets.
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    private static string DefaultPath()
    {
        var baseDirectory = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(baseDirectory, "shunchaki", "credentials.json");
    }
}
