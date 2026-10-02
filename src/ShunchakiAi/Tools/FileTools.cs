using System.Text.Json.Nodes;
using ShunchakiAi.Execution;

namespace ShunchakiAi.Tools;

public sealed class ReadFileTool(FileSystemService files) : ITool
{
    public string Name => "read_file";
    public string Description =>
        "Read a UTF-8 text file from the workspace. Output is prefixed with line numbers. " +
        "Large files are truncated; use start_line and max_lines to page through them.";
    public bool RequiresApproval => false;

    public JsonObject CreateInputSchema() => ToolInput.Schema(
        ("path", "string", "File path, relative to the workspace root.", true),
        ("start_line", "integer", "1-based line to start reading from. Default 1.", false),
        ("max_lines", "integer", "Maximum number of lines to return. Default: whole file.", false));

    public string Describe(JsonObject input) => $"Read {ToolInput.OptionalString(input, "path")}";

    public Task<ToolResult> ExecuteAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var path = ToolInput.RequireString(input, "path");
        var start = Math.Max(1, ToolInput.OptionalInt(input, "start_line", 1));
        var max = ToolInput.OptionalInt(input, "max_lines", 0);
        return Task.FromResult(new ToolResult(files.ReadText(path, start, max)));
    }
}

public sealed class ListDirectoryTool(FileSystemService files) : ITool
{
    public string Name => "list_directory";
    public string Description => "List the files and subdirectories of a directory in the workspace.";
    public bool RequiresApproval => false;

    public JsonObject CreateInputSchema() => ToolInput.Schema(
        ("path", "string", "Directory path relative to the workspace root. Use \".\" for the root.", true));

    public string Describe(JsonObject input) => $"List {ToolInput.OptionalString(input, "path") ?? "."}";

    public Task<ToolResult> ExecuteAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var path = ToolInput.OptionalString(input, "path") ?? ".";
        return Task.FromResult(new ToolResult(files.ListDirectory(path)));
    }
}

public sealed class WriteFileTool(FileSystemService files) : ITool
{
    public string Name => "write_file";
    public string Description =>
        "Create a new file or completely overwrite an existing one with the given content. " +
        "Prefer edit_file for changing part of an existing file.";
    public bool RequiresApproval => true;

    public JsonObject CreateInputSchema() => ToolInput.Schema(
        ("path", "string", "File path relative to the workspace root. Parent directories are created.", true),
        ("content", "string", "The complete new file content.", true));

    public string Describe(JsonObject input)
    {
        var content = ToolInput.OptionalString(input, "content") ?? string.Empty;
        return $"Write {ToolInput.OptionalString(input, "path")} ({content.Split('\n').Length} lines)";
    }

    public Task<ToolResult> ExecuteAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var path = ToolInput.RequireString(input, "path");
        var content = ToolInput.RequireString(input, "content");
        var full = files.WriteText(path, content);
        return Task.FromResult(new ToolResult($"Wrote {content.Length:N0} characters to {files.Relative(full)}."));
    }
}

public sealed class EditFileTool(FileSystemService files) : ITool
{
    public string Name => "edit_file";
    public string Description =>
        "Replace one exact, unique occurrence of old_string with new_string in a file. " +
        "old_string must match the file byte-for-byte (including indentation) and appear exactly once.";
    public bool RequiresApproval => true;

    public JsonObject CreateInputSchema() => ToolInput.Schema(
        ("path", "string", "File path relative to the workspace root.", true),
        ("old_string", "string", "Exact text to replace. Must be unique in the file.", true),
        ("new_string", "string", "Replacement text.", true));

    public string Describe(JsonObject input) => $"Edit {ToolInput.OptionalString(input, "path")}";

    public Task<ToolResult> ExecuteAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var path = ToolInput.RequireString(input, "path");
        var oldText = ToolInput.RequireString(input, "old_string");
        var newText = ToolInput.RequireString(input, "new_string");
        if (oldText.Length == 0)
        {
            throw new ToolInputException("old_string must not be empty. Use write_file to create a file.");
        }

        var full = files.ReplaceText(path, oldText, newText);
        return Task.FromResult(new ToolResult($"Edited {files.Relative(full)}."));
    }
}
