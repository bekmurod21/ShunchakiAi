using System.Text.Json.Nodes;
using ShunchakiAi.Sessions;

namespace ShunchakiAi.Tools;

/// <summary>
/// Lets the model write its own progress notes into the session work log. The notes are saved
/// for the user and handed to whichever model continues the work after a model switch.
/// </summary>
public sealed class RecordProgressTool(WorkLog workLog, Func<string?> currentModel) : ITool
{
    public string Name => "record_progress";
    public string Description =>
        "Record a short progress note in the session work log: what has been completed and what remains. " +
        "Call it after each significant step of a multi-step task. If the session switches to another model " +
        "(rate limit, token quota, outage), these notes are how the next model knows where to continue.";
    public bool RequiresApproval => false;

    public JsonObject CreateInputSchema() => ToolInput.Schema(
        ("done", "string", "What has been completed so far (files changed, results verified).", true),
        ("next_steps", "string", "What still needs to be done to finish the task. Empty if finished.", false));

    public string Describe(JsonObject input) => $"Progress: {ToolInput.OptionalString(input, "done")}";

    public Task<ToolResult> ExecuteAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var done = ToolInput.RequireString(input, "done");
        var next = ToolInput.OptionalString(input, "next_steps");
        workLog.Add(WorkLogKind.Progress, currentModel(),
            string.IsNullOrWhiteSpace(next) ? done : $"{done}\nNext: {next}");
        return Task.FromResult(new ToolResult("Progress recorded."));
    }
}
