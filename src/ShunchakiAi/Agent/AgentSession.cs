using System.Text.Json.Nodes;
using ShunchakiAi.Api;
using ShunchakiAi.Configuration;
using ShunchakiAi.Tools;

namespace ShunchakiAi.Agent;

/// <summary>
/// Drives the agentic loop: send the conversation, render the reply, execute any requested
/// tools, send the results back, and repeat until the model ends its turn.
/// </summary>
public sealed class AgentSession(AnthropicClient client, ToolRegistry tools, IAgentView view, AgentOptions options)
{
    private readonly Conversation _conversation = new();
    private readonly JsonArray _toolDefinitions = tools.CreateDefinitions();
    private readonly string _systemPrompt = SystemPrompt.Build(options.WorkingDirectory);

    public TokenUsage TotalUsage { get; private set; }

    public void Reset() => _conversation.Clear();

    /// <summary>Runs one user turn to completion. Returns false if the turn failed.</summary>
    public async Task<bool> RunTurnAsync(string userInput, CancellationToken cancellationToken)
    {
        var checkpoint = _conversation.Count;
        _conversation.AddUserText(userInput);
        var turnUsage = default(TokenUsage);
        var madeProgress = false;

        try
        {
            for (var iteration = 0; iteration < options.MaxToolIterations; iteration++)
            {
                var response = await view.ShowProgressAsync(
                    iteration == 0 ? "Thinking..." : "Working...",
                    () => client.CreateMessageAsync(BuildRequest(), cancellationToken)).ConfigureAwait(false);

                turnUsage += response.Usage;
                TotalUsage += response.Usage;

                if (response.StopReason == "refusal")
                {
                    // Do not keep a declined exchange in the history.
                    _conversation.TruncateTo(checkpoint);
                    view.ShowWarning("The model declined this request" +
                        (response.RefusalDetail is { } detail ? $": {detail}" : ".") + " Try rephrasing it.");
                    return false;
                }

                _conversation.AddAssistant(response.Content);
                madeProgress = true;
                Render(response.Content);

                var calls = response.ToolCalls.ToList();
                switch (response.StopReason)
                {
                    case "tool_use":
                        await RunToolsAsync(calls, cancellationToken).ConfigureAwait(false);
                        continue;

                    case "max_tokens" when calls.Count > 0:
                        // The tool input may be cut off mid-JSON: do not execute it.
                        _conversation.AddToolResults(calls.Select(c => (c.Id, ToolResult.Error(
                            "Your response hit max_tokens before this tool call was complete, so it was not run. " +
                            "Retry with smaller steps (e.g. several edit_file calls instead of one huge write)."))));
                        continue;

                    case "max_tokens":
                        view.ShowWarning("Response was cut off at the max_tokens limit. Say \"continue\" to keep going.");
                        return true;

                    case "pause_turn":
                        continue;

                    default:
                        return true;
                }
            }

            _conversation.CloseDanglingToolCalls("Stopped: tool iteration limit reached.");
            view.ShowWarning($"Stopped after {options.MaxToolIterations} tool iterations. Say \"continue\" to resume.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Recover(checkpoint, madeProgress, "Interrupted by the user before this tool ran.");
            view.ShowWarning("Interrupted.");
            return false;
        }
        catch (AnthropicApiException ex)
        {
            Recover(checkpoint, madeProgress, "Not run: the API request failed.");
            view.ShowError(Describe(ex));
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            Recover(checkpoint, madeProgress, "Not run: the API request failed.");
            view.ShowError($"Network error: {ex.Message}");
            return false;
        }
        finally
        {
            if (turnUsage != default)
            {
                view.ShowUsage(turnUsage, TotalUsage);
            }
        }
    }

    private MessageRequest BuildRequest() => new(
        options.Model,
        options.MaxTokens,
        _systemPrompt,
        _toolDefinitions,
        _conversation.Messages,
        options.Effort,
        ModelCapabilities.SupportsAdaptiveThinking(options.Model),
        options.UseServerFallback);

    private async Task RunToolsAsync(List<ToolCall> calls, CancellationToken cancellationToken)
    {
        // Run sequentially: approval prompts are interactive. Results are returned together.
        var results = new List<(string, ToolResult)>(calls.Count);
        foreach (var call in calls)
        {
            if (tools.TryGet(call.Name, out var tool))
            {
                view.ShowToolCall(tool, tool.Describe(call.Input));
            }

            ToolResult result;
            try
            {
                result = await tools.ExecuteAsync(call, view, options.AutoApprove, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Keep the results gathered so far; mark this and the remaining calls cancelled.
                results.AddRange(calls.Skip(results.Count).Select(c => (c.Id, ToolResult.Error("Interrupted by the user."))));
                _conversation.AddToolResults(results);
                throw;
            }

            view.ShowToolResult(result);
            results.Add((call.Id, result));
        }

        _conversation.AddToolResults(results);
    }

    private void Recover(int checkpoint, bool madeProgress, string reason)
    {
        if (madeProgress)
        {
            // Tools may already have changed files: keep the history so the model knows.
            _conversation.CloseDanglingToolCalls(reason);
        }
        else
        {
            _conversation.TruncateTo(checkpoint);
        }
    }

    private void Render(JsonArray content)
    {
        foreach (var block in content.OfType<JsonObject>())
        {
            switch (ModelResponse.BlockType(block))
            {
                case "text" when block["text"]?.GetValue<string>() is { Length: > 0 } text:
                    view.ShowAssistantText(text);
                    break;
                case "thinking" when block["thinking"]?.GetValue<string>() is { Length: > 0 } thinking:
                    view.ShowThinking(thinking);
                    break;
            }
        }
    }

    private static string Describe(AnthropicApiException ex) => ex.StatusCode switch
    {
        401 => "Authentication failed: check that ANTHROPIC_API_KEY is valid.",
        403 => $"Permission denied: {ex.Message}",
        404 => $"Not found (is the model name correct?): {ex.Message}",
        413 => "The request is too large. Use /clear to start a fresh conversation.",
        429 => "Rate limited, even after retries. Wait a moment and try again.",
        >= 500 => $"The API is having trouble right now: {ex.Message}",
        _ => ex.Message,
    };
}
