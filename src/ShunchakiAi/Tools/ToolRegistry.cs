using System.Text.Json.Nodes;
using ShunchakiAi.Api;
using ShunchakiAi.Execution;
using ShunchakiAi.Sessions;

namespace ShunchakiAi.Tools;

/// <summary>Asks the user whether a side-effecting tool call may run.</summary>
public interface IToolApprover
{
    Task<bool> ApproveAsync(ITool tool, string summary, CancellationToken cancellationToken);
}

/// <summary>
/// Owns the set of tools, produces their API definitions and executes calls. Every failure
/// is converted into an error <see cref="ToolResult"/> so the model can see it and recover;
/// only user cancellation propagates.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public static ToolRegistry CreateDefault(
        FileSystemService files, ShellExecutor shell, TimeSpan shellTimeout, WorkLog workLog, Func<string?> currentModel) => new(
    [
        new RecordProgressTool(workLog, currentModel),
        new ReadFileTool(files),
        new ListDirectoryTool(files),
        new WriteFileTool(files),
        new EditFileTool(files),
        new RunShellTool(shell, files, shellTimeout),
    ]);

    public IReadOnlyCollection<ITool> Tools => _tools.Values;

    /// <summary>Tool definitions in a deterministic order (keeps the prompt-cache prefix stable).</summary>
    public JsonArray CreateDefinitions()
    {
        var definitions = new JsonArray();
        foreach (var tool in _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            definitions.Add((JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.CreateInputSchema(),
            });
        }

        return definitions;
    }

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    public async Task<ToolResult> ExecuteAsync(ToolCall call, IToolApprover approver, bool autoApprove, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            return ToolResult.Error($"Unknown tool '{call.Name}'. Available: {string.Join(", ", _tools.Keys)}.");
        }

        try
        {
            if (tool.RequiresApproval && !autoApprove
                && !await approver.ApproveAsync(tool, tool.Describe(call.Input), cancellationToken).ConfigureAwait(false))
            {
                return ToolResult.Error("The user declined this action. Ask how they would like to proceed instead.");
            }

            return await tool.ExecuteAsync(call.Input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ToolInputException or WorkspaceAccessException or IOException
                                       or UnauthorizedAccessException or InvalidOperationException
                                       or InvalidDataException or ArgumentException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            return ToolResult.Error($"{tool.Name} failed: {ex.Message}");
        }
    }
}
