using ShunchakiAi.Api;
using ShunchakiAi.Tools;

namespace ShunchakiAi.Agent;

/// <summary>Everything the agent loop needs from the presentation layer.</summary>
public interface IAgentView : IToolApprover
{
    Task<T> ShowProgressAsync<T>(string status, Func<Task<T>> work);

    void ShowAssistantText(string markdown);

    void ShowThinking(string summary);

    void ShowToolCall(ITool tool, string summary);

    void ShowToolResult(ToolResult result);

    void ShowWarning(string message);

    void ShowInfo(string message);

    void ShowModelSwitch(string from, string to, string reason);

    void ShowError(string message);

    void ShowUsage(TokenUsage turn, TokenUsage total);
}
