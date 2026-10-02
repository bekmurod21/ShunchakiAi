using System.Text.Json.Nodes;

namespace ShunchakiAi.Tools;

public sealed record ToolResult(string Content, bool IsError = false)
{
    public static ToolResult Error(string message) => new(message, IsError: true);
}

/// <summary>A capability the model can invoke. Implementations hold no UI logic.</summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema for the tool input (built fresh on each call).</summary>
    JsonObject CreateInputSchema();

    /// <summary>Whether the user must confirm before the tool runs (side effects).</summary>
    bool RequiresApproval { get; }

    /// <summary>One-line human-readable summary of what a call will do.</summary>
    string Describe(JsonObject input);

    Task<ToolResult> ExecuteAsync(JsonObject input, CancellationToken cancellationToken);
}

/// <summary>Helpers for reading and validating model-supplied tool input.</summary>
public static class ToolInput
{
    public static string RequireString(JsonObject input, string name)
    {
        if (input[name] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        throw new ToolInputException($"Missing or invalid required string parameter '{name}'.");
    }

    public static string? OptionalString(JsonObject input, string name) =>
        input[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static int OptionalInt(JsonObject input, string name, int fallback)
    {
        if (input[name] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<double>(out var d) ? (int)d : fallback;
    }

    public static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in properties)
        {
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (isRequired)
            {
                required.Add((JsonNode)name);
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }
}

public sealed class ToolInputException(string message) : Exception(message);
