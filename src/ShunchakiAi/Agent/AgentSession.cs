using System.Text.Json.Nodes;
using ShunchakiAi.Api;
using ShunchakiAi.Configuration;
using ShunchakiAi.Sessions;
using ShunchakiAi.Tools;

namespace ShunchakiAi.Agent;

/// <summary>
/// Drives the agentic loop: send the conversation, render the reply, execute any requested
/// tools, send the results back, and repeat until the model ends its turn.
///
/// The work survives model failures: when a model hits its rate limit / token quota, is
/// overloaded, cannot fit the conversation or declines, the same request continues on the next
/// model in the chain. Every model receives the identical, append-only history; a handoff note
/// with the work log tells the new model it is taking over. The session (history, exact system
/// prompt, work log) is saved after every step so it can also be resumed later.
/// </summary>
public sealed class AgentSession
{
    private const string ResumedReason = "session resumed";

    private const string ContinuePrompt =
        "Your previous response was cut off by the output token limit. Continue exactly where you stopped; " +
        "do not repeat what you already wrote.";

    private const int MaxRetries = 3;

    private readonly ProviderRegistry _providers;
    private readonly ToolRegistry _tools;
    private readonly IAgentView _view;
    private readonly AgentOptions _options;
    private readonly ModelRouter _router;
    private readonly WorkLog _workLog;
    private readonly SessionStore _store;
    private readonly JsonArray _toolDefinitions;

    private Conversation _conversation = new();
    private string _systemPrompt;
    private DateTimeOffset _created;
    private string? _lastModel;
    private string? _switchReason;
    private JsonObject? _unansweredNote;
    private string? _handoffFor;
    private bool _saveFailed;

    public AgentSession(
        ProviderRegistry providers, ToolRegistry tools, IAgentView view, AgentOptions options,
        ModelRouter router, WorkLog workLog, SessionStore store)
    {
        _providers = providers;
        _tools = tools;
        _view = view;
        _options = options;
        _router = router;
        _workLog = workLog;
        _store = store;
        _toolDefinitions = tools.CreateDefinitions();
        _systemPrompt = SystemPrompt.Build(options.WorkingDirectory);
        _created = DateTimeOffset.Now;
        SessionId = SessionStore.NewId();
    }

    public string SessionId { get; private set; }

    /// <summary>The model serving the current request (or the last one that answered).</summary>
    public string? ActiveModel { get; private set; }

    public TokenUsage TotalUsage { get; private set; }

    public Dictionary<string, TokenUsage> UsageByModel { get; } = new(StringComparer.Ordinal);

    public ModelRouter Router => _router;

    public string SessionPath => _store.PathFor(SessionId);

    /// <summary>Starts a fresh conversation (new session id, empty history and work log).</summary>
    public void Reset()
    {
        _conversation = new Conversation();
        _workLog.Reset();
        _router.ResetConversation();
        _systemPrompt = SystemPrompt.Build(_options.WorkingDirectory);
        _created = DateTimeOffset.Now;
        _lastModel = null;
        _unansweredNote = null;
        _handoffFor = null;
        SessionId = SessionStore.NewId();
    }

    /// <summary>
    /// Continues a saved session. The stored system prompt is reused verbatim, so the request
    /// prefix is byte-identical to what the earlier models saw.
    /// </summary>
    public void Load(SessionSnapshot snapshot)
    {
        _conversation = new Conversation(snapshot.Messages);
        _workLog.Reset(snapshot.WorkLog);
        _router.ResetConversation();
        _systemPrompt = snapshot.SystemPrompt;
        _created = snapshot.Created;
        _lastModel = snapshot.LastModel;
        _unansweredNote = null;
        _handoffFor = null;
        SessionId = snapshot.Id;

        // A session saved mid-turn may end on an assistant tool call that never ran.
        _conversation.CloseDanglingToolCalls("Not run: the session was closed before this tool call executed.");
        _switchReason = ResumedReason;
    }

    public int MessageCount => _conversation.Count;

    /// <summary>Runs one user turn to completion. Returns false if the turn failed.</summary>
    public async Task<bool> RunTurnAsync(string userInput, CancellationToken cancellationToken)
    {
        var checkpoint = _conversation.Count;
        var workLogCheckpoint = _workLog.Entries.Count;
        _router.BeginTurn();
        _conversation.AddUserText(userInput);
        _workLog.Add(WorkLogKind.Request, null, userInput);
        var turnUsage = default(TokenUsage);
        var madeProgress = false;
        var continuations = 0;

        try
        {
            for (var iteration = 0; iteration < _options.MaxToolIterations; iteration++)
            {
                var (response, model) = await RequestWithFailoverAsync(iteration == 0, cancellationToken).ConfigureAwait(false);
                if (response is null)
                {
                    // Every model declined: do not keep the refused exchange in the history.
                    _conversation.TruncateTo(checkpoint);
                    _unansweredNote = null;
                    _handoffFor = null;
                    _workLog.Add(WorkLogKind.Problem, model, "Request declined by every available model.", isError: true);
                    _view.ShowWarning("Every available model declined this request. Try rephrasing it.");
                    return false;
                }

                turnUsage += response.Usage;
                TotalUsage += response.Usage;
                UsageByModel[model] = UsageByModel.GetValueOrDefault(model) + response.Usage;

                _conversation.AddAssistant(response.Content);
                madeProgress = true;
                Render(response, model);
                Save();

                var calls = response.ToolCalls.ToList();
                switch (response.StopReason)
                {
                    case "tool_use":
                        await RunToolsAsync(calls, model, cancellationToken).ConfigureAwait(false);
                        Save();
                        continue;

                    case "max_tokens" when calls.Count > 0:
                        // The tool input may be cut off mid-JSON: do not execute it.
                        _conversation.AddToolResults(calls.Select(c => (c.Id, ToolResult.Error(
                            "Your response hit max_tokens before this tool call was complete, so it was not run. " +
                            "Retry with smaller steps (e.g. several edit_file calls instead of one huge write)."))));
                        continue;

                    case "max_tokens" when continuations < _options.MaxAutoContinue:
                        continuations++;
                        _view.ShowInfo($"Output limit reached; continuing automatically ({continuations}/{_options.MaxAutoContinue})...");
                        _conversation.AddUserText(ContinuePrompt);
                        continue;

                    case "max_tokens":
                        _view.ShowWarning("Response was cut off at the max_tokens limit. Say \"continue\" to keep going.");
                        return true;

                    case "pause_turn":
                        continue;

                    default:
                        return true;
                }
            }

            _conversation.CloseDanglingToolCalls("Stopped: tool iteration limit reached.");
            _workLog.Add(WorkLogKind.Problem, ActiveModel, $"Stopped after {_options.MaxToolIterations} tool iterations.");
            _view.ShowWarning($"Stopped after {_options.MaxToolIterations} tool iterations. Say \"continue\" to resume.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Recover(checkpoint, workLogCheckpoint, madeProgress, "Interrupted by the user before this tool ran.");
            _workLog.Add(WorkLogKind.Problem, ActiveModel, "Interrupted by the user.");
            _view.ShowWarning("Interrupted.");
            return false;
        }
        catch (AllModelsFailedException ex)
        {
            Recover(checkpoint, workLogCheckpoint, madeProgress, "Not run: no model could serve the request.");
            _workLog.Add(WorkLogKind.Problem, ActiveModel, ex.Message, isError: true);
            _view.ShowError(ex.Message);
            if (madeProgress)
            {
                _view.ShowInfo($"Progress is saved. Resume later with: shunchaki --session {SessionId}");
            }

            return false;
        }
        finally
        {
            Save();
            if (turnUsage != default)
            {
                _view.ShowUsage(turnUsage, TotalUsage);
            }
        }
    }

    /// <summary>
    /// Sends the conversation to the best available model, moving down the chain (or waiting
    /// for a cooldown) until one answers. Returns a null response only if every model refused.
    /// </summary>
    private async Task<(ModelResponse? Response, string Model)> RequestWithFailoverAsync(bool firstStep, CancellationToken cancellationToken)
    {
        string? lastError = null;
        DateTimeOffset? failingSince = null;
        while (true)
        {
            var now = DateTimeOffset.Now;
            if (lastError is not null)
            {
                failingSince ??= now;
            }

            var (model, wait) = _router.Select(now);
            if (model is null)
            {
                if (!_router.AnyConfigured)
                {
                    throw new AllModelsFailedException("No API key is configured. Use /login to enter a Claude or Gemini key.");
                }

                if (lastError is null)
                {
                    return (null, ActiveModel ?? _options.Model);
                }

                throw new AllModelsFailedException($"No model in the chain can continue: {lastError}");
            }

            if (wait > TimeSpan.Zero)
            {
                // Give up once the models have been failing for longer than the allowed wait.
                if (failingSince is { } since && now + wait - since > _options.MaxWait)
                {
                    throw new AllModelsFailedException(
                        $"Every model stayed unavailable for over {_options.MaxWait.TotalMinutes:0.#} min. Last error: {lastError}");
                }

                _workLog.Add(WorkLogKind.Problem, null, $"All models are busy; waiting {wait.TotalSeconds:0}s for {model}.");
                await _view.ShowProgressAsync(
                    $"All models are rate-limited or busy; retrying {model} in {wait.TotalSeconds:0}s...",
                    async () => { await Task.Delay(wait, cancellationToken).ConfigureAwait(false); return true; })
                    .ConfigureAwait(false);
                continue;
            }

            PrepareHandoff(model);
            ActiveModel = model;

            try
            {
                var retries = _router.HasAlternative(model, now) ? 0 : MaxRetries;
                var label = firstStep ? "Thinking" : "Working";
                var response = await _view.ShowProgressAsync(
                    $"{label} ({model})...",
                    () => _providers.For(model).CreateMessageAsync(BuildRequest(model), retries, cancellationToken)).ConfigureAwait(false);

                if (response.StopReason == "refusal")
                {
                    var detail = response.RefusalDetail is { } d ? $": {d}" : string.Empty;
                    _router.SkipForTurn(model);
                    lastError = null;
                    AddSwitchReason($"{model} declined the request{detail}");
                    _view.ShowWarning($"{model} declined this request{detail}.");
                    continue;
                }

                _router.ReportSuccess(model);
                _lastModel = model;
                _switchReason = null;
                _unansweredNote = null;
                _handoffFor = null;
                return (response, model);
            }
            catch (ModelApiException ex)
            {
                var failure = FailoverPolicy.Classify(ex);
                lastError = $"{model}: {failure.Reason}";
                OnModelFailed(model, failure);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                var failure = FailoverPolicy.Network(ex);
                lastError = $"{model}: {failure.Reason}";
                OnModelFailed(model, failure);
            }
        }
    }

    /// <summary>Every failure since the last answer explains why the work moved on.</summary>
    private void AddSwitchReason(string reason) =>
        _switchReason = _switchReason is null or ResumedReason ? reason : $"{_switchReason}; {reason}";

    private void OnModelFailed(string model, Failure failure)
    {
        _router.ReportFailure(model, failure, DateTimeOffset.Now);
        AddSwitchReason($"{model} became unavailable ({failure.Reason})");
        _workLog.Add(WorkLogKind.Problem, model, failure.Reason, isError: true);
        _view.ShowWarning($"{model}: {failure.Reason}.");
    }

    /// <summary>
    /// When a different model is about to continue the conversation, append a handoff note to
    /// the pending user message. The history itself is sent unchanged (including the previous
    /// model's thinking blocks; a model that cannot read them simply ignores them), so the new
    /// model sees exactly the same context plus a summary of the recorded work.
    /// </summary>
    private void PrepareHandoff(string model)
    {
        // _lastModel is the last model that actually answered; failed attempts never count.
        if (_lastModel is null || _handoffFor == model)
        {
            return;
        }

        if (_unansweredNote is not null)
        {
            // A previous handoff attempt failed before any model answered: replace its note.
            _conversation.RemoveUnansweredNote(_unansweredNote);
            _unansweredNote = null;
            _handoffFor = null;
        }

        if (_lastModel == model)
        {
            return;
        }

        var reason = _switchReason ?? $"{model} is available again and is preferred in the model chain";
        _unansweredNote = _conversation.AppendNoteToLastUserMessage(
            $"""
            <handoff>
            You ({model}) are taking over this session from {_lastModel}. Reason: {reason}.
            The complete conversation above is the real history of this task; the previous model's private
            reasoning may not be visible to you. Continue the work from where it stopped - do not restart it,
            and do not redo actions that already succeeded. Check files if you are unsure of their current state.

            Work log:
            {_workLog.BuildHandoffSummary()}
            </handoff>
            """);

        _workLog.Add(WorkLogKind.ModelSwitch, model, $"{_lastModel} → {model} ({reason})");
        _view.ShowModelSwitch(_lastModel, model, reason);
        _handoffFor = model;
    }

    private MessageRequest BuildRequest(string model) => new MessageRequest(
        model,
        _options.MaxTokens,
        _systemPrompt,
        _toolDefinitions,
        _conversation.Messages,
        _options.Effort,
        ModelCapabilities.SupportsAdaptiveThinking(model),
        _options.UseServerFallback && ModelCapabilities.SupportsServerFallback(model))
    {
        // CLI backends report each action they take; show it live and keep it in the work log
        // so the next model knows what was done.
        Activity = action =>
        {
            _view.ShowActivity(model, action);
            _workLog.Add(WorkLogKind.Tool, model, action, isError: action.StartsWith('⚠'));
        },
    };

    private async Task RunToolsAsync(List<ToolCall> calls, string model, CancellationToken cancellationToken)
    {
        // Run sequentially: approval prompts are interactive. Results are returned together.
        var results = new List<(string, ToolResult)>(calls.Count);
        foreach (var call in calls)
        {
            var summary = call.Name;
            if (_tools.TryGet(call.Name, out var tool))
            {
                summary = tool.Describe(call.Input);
                _view.ShowToolCall(tool, summary);
            }

            ToolResult result;
            try
            {
                result = await _tools.ExecuteAsync(call, _view, _options.AutoApprove, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Keep the results gathered so far; mark this and the remaining calls cancelled.
                results.AddRange(calls.Skip(results.Count).Select(c => (c.Id, ToolResult.Error("Interrupted by the user."))));
                _conversation.AddToolResults(results);
                throw;
            }

            _view.ShowToolResult(result);
            if (call.Name != "record_progress")
            {
                var firstLine = result.Content.Split('\n', 2)[0];
                _workLog.Add(WorkLogKind.Tool, model, result.IsError ? $"{summary} → {firstLine}" : summary, result.IsError);
            }

            results.Add((call.Id, result));
        }

        _conversation.AddToolResults(results);
    }

    private void Recover(int checkpoint, int workLogCheckpoint, bool madeProgress, string reason)
    {
        if (madeProgress)
        {
            // Tools may already have changed files: keep the history so the next model knows.
            _conversation.CloseDanglingToolCalls(reason);
        }
        else
        {
            _conversation.TruncateTo(checkpoint);
            _unansweredNote = null;
            _handoffFor = null;
            _workLog.Reset(_workLog.Entries.Take(workLogCheckpoint).ToList());
        }
    }

    private void Render(ModelResponse response, string model)
    {
        foreach (var thought in response.Thoughts)
        {
            _view.ShowThinking(thought);
        }

        foreach (var block in response.Content.OfType<JsonObject>())
        {
            switch (ModelResponse.BlockType(block))
            {
                case "text" when block["text"]?.GetValue<string>() is { Length: > 0 } text:
                    _view.ShowAssistantText(text);
                    _workLog.Add(WorkLogKind.Answer, model, text);
                    break;
                case "thinking" when block["thinking"]?.GetValue<string>() is { Length: > 0 } thinking:
                    _view.ShowThinking(thinking);
                    break;
            }
        }
    }

    private void Save()
    {
        if (_conversation.Count == 0)
        {
            return;
        }

        try
        {
            _store.Save(new SessionSnapshot(SessionId, _created, _systemPrompt, _lastModel, _conversation.Messages, _workLog.Entries), _workLog);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_saveFailed)
            {
                _saveFailed = true;
                _view.ShowWarning($"Could not save the session to {SessionPath}: {ex.Message}");
            }
        }
    }

}

/// <summary>No model in the chain could serve the request.</summary>
public sealed class AllModelsFailedException(string message) : Exception(message);
