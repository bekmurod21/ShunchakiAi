using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ShunchakiAi.Sessions;

public enum WorkLogKind
{
    Request,
    Tool,
    Progress,
    ModelSwitch,
    Answer,
    Problem,
}

public sealed record WorkLogEntry(DateTimeOffset Time, WorkLogKind Kind, string? Model, string Text, bool IsError = false);

/// <summary>
/// Chronological record of what was done in a session: requests, tool actions, progress notes
/// written by the model, model switches and final answers. It is saved next to the conversation
/// for the user, and its summary is handed to the next model whenever the model changes, so the
/// work continues from where it stopped instead of starting over.
/// </summary>
public sealed class WorkLog
{
    private const int MaxTextLength = 600;
    private readonly List<WorkLogEntry> _entries = [];

    public IReadOnlyList<WorkLogEntry> Entries => _entries;

    public void Add(WorkLogKind kind, string? model, string text, bool isError = false)
    {
        text = text.Trim();
        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength] + "…";
        }

        _entries.Add(new WorkLogEntry(DateTimeOffset.Now, kind, model, text, isError));
    }

    public void Reset(IEnumerable<WorkLogEntry>? entries = null)
    {
        _entries.Clear();
        if (entries is not null)
        {
            _entries.AddRange(entries);
        }
    }

    /// <summary>
    /// Compact summary for a model taking over: the latest user request, every progress note,
    /// and the most recent actions.
    /// </summary>
    public string BuildHandoffSummary(int maxActions = 25)
    {
        var text = new StringBuilder();

        var lastRequest = _entries.LastOrDefault(e => e.Kind == WorkLogKind.Request);
        if (lastRequest is not null)
        {
            text.AppendLine($"Current user request: {lastRequest.Text}");
        }

        var progress = _entries.Where(e => e.Kind == WorkLogKind.Progress).ToList();
        if (progress.Count > 0)
        {
            text.AppendLine("Progress notes so far (oldest first):");
            foreach (var entry in progress.TakeLast(15))
            {
                text.AppendLine($"- [{entry.Model}] {entry.Text}");
            }
        }

        var actions = _entries.Where(e => e.Kind == WorkLogKind.Tool).TakeLast(maxActions).ToList();
        if (actions.Count > 0)
        {
            text.AppendLine("Most recent actions:");
            foreach (var entry in actions)
            {
                text.AppendLine($"- {(entry.IsError ? "FAILED " : string.Empty)}{entry.Text} [{entry.Model}]");
            }
        }

        return text.Length == 0 ? "(no work recorded yet)" : text.ToString().TrimEnd();
    }

    public string ToMarkdown(string sessionId, string workspace)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Work log - session {sessionId}");
        md.AppendLine();
        md.AppendLine($"Workspace: `{workspace}`");
        md.AppendLine();

        foreach (var entry in _entries)
        {
            var time = entry.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var model = entry.Model is null ? string.Empty : $" `{entry.Model}`";
            var line = entry.Text.Replace("\n", "\n  ");
            md.AppendLine(entry.Kind switch
            {
                WorkLogKind.Request => $"\n## {time} - Request\n\n{line}\n",
                WorkLogKind.Tool => $"- {time}{model} {(entry.IsError ? "❌" : "✅")} {line}",
                WorkLogKind.Progress => $"- {time}{model} 📝 **Progress:** {line}",
                WorkLogKind.ModelSwitch => $"- {time} 🔁 **Model switch:** {line}",
                WorkLogKind.Answer => $"- {time}{model} 💬 {line}",
                WorkLogKind.Problem => $"- {time}{model} ⚠️ {line}",
                _ => $"- {time} {line}",
            });
        }

        return md.ToString();
    }

    public JsonArray ToJson()
    {
        var array = new JsonArray();
        foreach (var e in _entries)
        {
            var node = new JsonObject
            {
                ["time"] = e.Time.ToString("O", CultureInfo.InvariantCulture),
                ["kind"] = e.Kind.ToString(),
                ["text"] = e.Text,
            };
            if (e.Model is not null)
            {
                node["model"] = e.Model;
            }

            if (e.IsError)
            {
                node["error"] = true;
            }

            array.Add((JsonNode)node);
        }

        return array;
    }

    public static IEnumerable<WorkLogEntry> FromJson(JsonArray? array)
    {
        foreach (var node in array?.OfType<JsonObject>() ?? [])
        {
            if (!Enum.TryParse<WorkLogKind>(node["kind"]?.GetValue<string>(), out var kind))
            {
                continue;
            }

            _ = DateTimeOffset.TryParse(node["time"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var time);
            yield return new WorkLogEntry(
                time,
                kind,
                node["model"]?.GetValue<string>(),
                node["text"]?.GetValue<string>() ?? string.Empty,
                node["error"]?.GetValue<bool>() ?? false);
        }
    }
}
