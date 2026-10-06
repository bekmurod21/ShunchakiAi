using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShunchakiAi.Sessions;

/// <summary>Everything needed to continue a conversation later, on any model.</summary>
public sealed record SessionSnapshot(
    string Id,
    DateTimeOffset Created,
    string SystemPrompt,
    string? LastModel,
    JsonArray Messages,
    IReadOnlyList<WorkLogEntry> WorkLog);

public sealed record SessionInfo(string Id, DateTimeOffset Updated, string? FirstRequest);

/// <summary>
/// Saves sessions under <c>&lt;workspace&gt;/.shunchaki/sessions/&lt;id&gt;/</c>:
/// <c>session.json</c> (full message history, the exact system prompt and the work log) and
/// <c>worklog.md</c> (human-readable). The history is stored exactly as sent to the API, so a
/// resumed or switched-to model sees the same context. Writes are atomic.
/// </summary>
public sealed class SessionStore
{
    private const string FileName = "session.json";
    private const string WorkLogFile = "worklog.md";

    private readonly string _workspace;
    private readonly string _root;

    public SessionStore(string workspace)
    {
        _workspace = workspace;
        _root = Path.Combine(workspace, ".shunchaki", "sessions");
    }

    public string Directory => _root;

    public static string NewId() =>
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4];

    public string PathFor(string id) => System.IO.Path.Combine(_root, id);

    public void Save(SessionSnapshot snapshot, WorkLog workLog)
    {
        var directory = PathFor(snapshot.Id);
        System.IO.Directory.CreateDirectory(directory);
        EnsureIgnored();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("id", snapshot.Id);
            writer.WriteString("created", snapshot.Created.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("updated", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("workspace", _workspace);
            writer.WriteString("lastModel", snapshot.LastModel);
            writer.WriteString("systemPrompt", snapshot.SystemPrompt);
            writer.WritePropertyName("workLog");
            workLog.ToJson().WriteTo(writer);
            writer.WritePropertyName("messages");
            snapshot.Messages.WriteTo(writer);
            writer.WriteEndObject();
        }

        WriteAtomic(System.IO.Path.Combine(directory, FileName), buffer.ToArray());
        WriteAtomic(System.IO.Path.Combine(directory, WorkLogFile),
            Encoding.UTF8.GetBytes(workLog.ToMarkdown(snapshot.Id, _workspace)));
    }

    public SessionSnapshot Load(string id)
    {
        if (id.Contains('/') || id.Contains('\\') || id.Contains(".."))
        {
            throw new ArgumentException($"Invalid session id '{id}'.");
        }

        var file = System.IO.Path.Combine(PathFor(id), FileName);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"Session '{id}' not found in {_root}.");
        }

        if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root)
        {
            throw new InvalidDataException($"Session file {file} is corrupt.");
        }

        var messages = root["messages"] as JsonArray ?? [];
        root.Remove("messages");
        _ = DateTimeOffset.TryParse(root["created"]?.GetValue<string>(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var created);

        return new SessionSnapshot(
            root["id"]?.GetValue<string>() ?? id,
            created,
            root["systemPrompt"]?.GetValue<string>() ?? throw new InvalidDataException("Session has no system prompt."),
            root["lastModel"]?.GetValue<string>(),
            messages,
            WorkLog.FromJson(root["workLog"] as JsonArray).ToList());
    }

    /// <summary>Most recently updated sessions first.</summary>
    public IReadOnlyList<SessionInfo> List(int max = 20)
    {
        if (!System.IO.Directory.Exists(_root))
        {
            return [];
        }

        return new DirectoryInfo(_root).EnumerateDirectories()
            .Select(d => new FileInfo(System.IO.Path.Combine(d.FullName, FileName)))
            .Where(f => f.Exists)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(max)
            .Select(f => new SessionInfo(f.Directory!.Name, f.LastWriteTime, ReadFirstRequest(f.Directory.FullName)))
            .ToList();
    }

    public string? LatestId() => List(1).FirstOrDefault()?.Id;

    private static string? ReadFirstRequest(string directory)
    {
        var log = System.IO.Path.Combine(directory, WorkLogFile);
        if (!File.Exists(log))
        {
            return null;
        }

        // The first non-empty line after the first "Request" heading.
        var lines = File.ReadLines(log).SkipWhile(l => !l.EndsWith("- Request", StringComparison.Ordinal)).Skip(1);
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        return first is { Length: > 80 } ? first[..80] + "…" : first;
    }

    private void EnsureIgnored()
    {
        // Keep session data out of the user's git repository.
        var ignore = System.IO.Path.Combine(_workspace, ".shunchaki", ".gitignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(ignore, "*\n");
        }
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
