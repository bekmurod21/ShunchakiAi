using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShunchakiAi.Configuration;
using ShunchakiAi.Execution;

namespace ShunchakiAi.Api;

/// <summary>
/// Shared logic for backends that are complete coding agents (Claude Code, Gemini CLI) run as
/// subprocesses with the user's own subscription login. One request = one full agent turn: the
/// CLI reads/edits files and runs commands itself, then returns a final answer, which becomes an
/// ordinary assistant text turn in the shared history.
/// </summary>
public abstract class CliAgentProvider(string executable, AgentOptions options) : IModelProvider
{
    /// <summary>Private fields on the canonical text block (stripped before any HTTP API call).</summary>
    public const string BackendField = MessageRequest.PrivateFieldPrefix + "cli_backend";
    public const string SessionField = MessageRequest.PrivateFieldPrefix + "cli_session";

    private const int MaxTranscriptChars = 80_000;

    /// <summary>
    /// API keys are removed from the child's environment so the CLI uses its subscription
    /// login instead of silently billing an API key.
    /// </summary>
    private static readonly string[] StrippedEnvironment =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "GEMINI_API_KEY", "GOOGLE_API_KEY"];

    protected const string Instructions =
        "You are being run by Shunchaki, a terminal coding agent that hands a task between several AI models " +
        "when one reaches its usage limit. Work in the current directory until the task is fully finished. " +
        "End your reply with a short summary: files you changed, commands you ran and their results, and anything " +
        "still left to do. That summary is what the next model will see.";

    public abstract string Name { get; }

    protected string Executable => executable;

    protected AgentOptions Options => options;

    public abstract Task<ModelResponse> CreateMessageAsync(MessageRequest request, int maxRetries, CancellationToken cancellationToken);

    public Task<KeyCheck> CheckKeyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new KeyCheck(KeyCheckResult.Unknown, "login is checked on first use"));

    public void Dispose()
    {
    }

    protected Task<CliResult> RunAsync(IEnumerable<string> arguments, string prompt, Action<string>? onLine, CancellationToken cancellationToken) =>
        CliProcess.RunAsync(executable, arguments, options.WorkingDirectory, prompt, StrippedEnvironment, onLine,
            options.CliTimeout, cancellationToken);

    /// <summary>"claude-code:opus" → "opus"; "claude-code" → null (the CLI's default model).</summary>
    protected static string? SubModel(string model) =>
        model.IndexOf(':') is var i and > 0 && i < model.Length - 1 ? model[(i + 1)..] : null;

    protected static IEnumerable<string> ExtraArguments(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? [] : configured.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    protected JsonArray ResultContent(string text, string? sessionId)
    {
        var block = new JsonObject
        {
            ["type"] = "text",
            ["text"] = string.IsNullOrWhiteSpace(text) ? "(no response)" : text,
            [BackendField] = Name,
        };
        if (sessionId is not null)
        {
            block[SessionField] = sessionId;
        }

        return new JsonArray(block);
    }

    /// <summary>
    /// Converts a CLI failure into the same exception shape as an HTTP API error, so the
    /// failover policy treats "usage limit reached" exactly like an API rate limit.
    /// </summary>
    protected ModelApiException Failure(string message, int? statusHint = null)
    {
        message = message.Trim();
        if (message.Length == 0)
        {
            message = "the CLI exited without a result";
        }

        var status = statusHint ?? Classify(message);
        TimeSpan? retryAfter = null;
        if (status == 429)
        {
            // Claude Code reports "...usage limit reached|<unix time it resets>".
            var pipe = message.LastIndexOf('|');
            if (pipe > 0 && long.TryParse(message[(pipe + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                retryAfter = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
            }
            else if (Contains(message, "usage limit") || Contains(message, "hit your limit") || Contains(message, "resets"))
            {
                // Subscription windows last hours; don't re-try every minute.
                retryAfter = TimeSpan.FromMinutes(15);
            }
        }

        return new ModelApiException(Name, status, "cli_error", message.Length > 400 ? message[..400] + "…" : message) { RetryAfter = retryAfter };
    }

    private static int Classify(string message)
    {
        if (Contains(message, "usage limit") || Contains(message, "limit reached") || Contains(message, "hit your limit")
            || Contains(message, "rate limit") || Contains(message, "quota") || Contains(message, "RESOURCE_EXHAUSTED")
            || Contains(message, "429"))
        {
            return 429;
        }

        if (Contains(message, "login") || Contains(message, "log in") || Contains(message, "logged in")
            || Contains(message, "authenticat") || Contains(message, "credentials") || Contains(message, "invalid api key")
            || Contains(message, "oauth"))
        {
            return 401;
        }

        if (Contains(message, "overloaded") || Contains(message, "529") || Contains(message, "503"))
        {
            return 529;
        }

        // Anything else: don't keep retrying this backend for the current conversation.
        return 400;
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The prompt for a CLI that has not seen this conversation: instructions, the shared
    /// history (text, tool calls and results from any model) and the current user message,
    /// which carries the handoff note with the work log when the model changed.
    /// </summary>
    protected static string BuildTranscriptPrompt(MessageRequest request, bool includeInstructions)
    {
        var messages = request.Messages.OfType<JsonObject>().ToList();
        var last = messages.LastOrDefault();
        var currentTexts = last?["role"]?.GetValue<string>() == "user" ? Texts(last).ToList() : [];

        var entries = new List<string>();
        foreach (var message in messages)
        {
            var isCurrent = ReferenceEquals(message, last) && currentTexts.Count > 0;
            var role = message["role"]?.GetValue<string>() == "assistant" ? "assistant" : "user";
            foreach (var block in Blocks(message))
            {
                var type = block["type"]?.GetValue<string>();
                var entry = type switch
                {
                    "text" when !isCurrent => $"[{role}] {block["text"]?.GetValue<string>()}",
                    "tool_use" => $"[assistant called tool {block["name"]?.GetValue<string>()}] {Clip(block["input"]?.ToJsonString() ?? "{}", 400)}",
                    "tool_result" => $"[tool result{(block["is_error"]?.GetValue<bool>() == true ? " - ERROR" : string.Empty)}] {Clip(ResultText(block["content"]), 1_200)}",
                    _ => null,
                };
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        var prompt = new StringBuilder();
        if (includeInstructions)
        {
            prompt.AppendLine(Instructions).AppendLine();
        }

        if (entries.Count > 0)
        {
            prompt.AppendLine("This task was already being worked on in this directory. Conversation and actions so far:");
            prompt.AppendLine("<history>");
            AppendBounded(prompt, entries);
            prompt.AppendLine("</history>").AppendLine();
        }

        prompt.AppendLine(currentTexts.Count > 0
            ? string.Join("\n\n", currentTexts)
            : "Continue the task from where it stopped. Do not redo actions that already succeeded.");
        return prompt.ToString();
    }

    /// <summary>User text sent after the CLI's own last answer (used when resuming its session).</summary>
    protected static string? NewTextSince(MessageRequest request, string backend, out string? sessionId)
    {
        sessionId = null;
        var messages = request.Messages.OfType<JsonObject>().ToList();
        var lastAssistant = messages.FindLastIndex(m => m["role"]?.GetValue<string>() == "assistant");
        if (lastAssistant < 0)
        {
            return null;
        }

        var first = Blocks(messages[lastAssistant]).FirstOrDefault();
        if (first?[BackendField]?.GetValue<string>() != backend || first[SessionField]?.GetValue<string>() is not { } session)
        {
            return null;
        }

        var texts = messages.Skip(lastAssistant + 1).SelectMany(Texts).ToList();
        if (texts.Count == 0)
        {
            return null;
        }

        sessionId = session;
        return string.Join("\n\n", texts);
    }

    private static void AppendBounded(StringBuilder prompt, List<string> entries)
    {
        // Keep the first entry (the original request) and as many recent entries as fit.
        var kept = new List<string>();
        var budget = MaxTranscriptChars - entries[0].Length;
        for (var i = entries.Count - 1; i > 0 && budget > 0; i--)
        {
            budget -= entries[i].Length;
            if (budget >= 0)
            {
                kept.Add(entries[i]);
            }
        }

        kept.Reverse();
        prompt.AppendLine(entries[0]);
        var omitted = entries.Count - 1 - kept.Count;
        if (omitted > 0)
        {
            prompt.AppendLine($"[... {omitted} earlier entries omitted ...]");
        }

        foreach (var entry in kept)
        {
            prompt.AppendLine(entry);
        }
    }

    private static IEnumerable<JsonObject> Blocks(JsonObject message) => message["content"] switch
    {
        JsonArray array => array.OfType<JsonObject>(),
        JsonValue value when value.TryGetValue<string>(out var text) => [new JsonObject { ["type"] = "text", ["text"] = text }],
        _ => [],
    };

    private static IEnumerable<string> Texts(JsonObject message) =>
        Blocks(message).Where(b => b["type"]?.GetValue<string>() == "text")
            .Select(b => b["text"]?.GetValue<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))!;

    private static string ResultText(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Select(b => b["text"]?.GetValue<string>()).Where(t => t is not null)),
        _ => string.Empty,
    };

    protected static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>A short human description of a tool call input (command, path, pattern...).</summary>
    protected static string SummarizeInput(JsonNode? input)
    {
        if (input is not JsonObject obj)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "command", "file_path", "path", "pattern", "url", "query", "description" })
        {
            if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                return Clip(text.ReplaceLineEndings(" "), 120);
            }
        }

        return Clip(obj.ToJsonString(), 120);
    }

    protected static long Long(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<long>(out var result) ? result : 0;
}

/// <summary>
/// Claude Code (<c>claude -p --output-format stream-json</c>) using the user's Claude Pro/Max
/// login. Resumes its own session (<c>--resume</c>) when it produced the previous answer, so its
/// full internal context is kept; otherwise it receives the shared transcript.
/// </summary>
public sealed class ClaudeCodeProvider(string executable, AgentOptions options) : CliAgentProvider(executable, options)
{
    public override string Name => ModelProviders.ClaudeCode;

    public override async Task<ModelResponse> CreateMessageAsync(MessageRequest request, int maxRetries, CancellationToken cancellationToken)
    {
        var resumeText = NewTextSince(request, Name, out var sessionId);
        try
        {
            return resumeText is not null
                ? await RunTurnAsync(request, resumeText, sessionId, cancellationToken).ConfigureAwait(false)
                : await RunTurnAsync(request, BuildTranscriptPrompt(request, includeInstructions: false), null, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelApiException ex) when (sessionId is not null && ex.StatusCode == 400)
        {
            // The saved session is gone (other machine, cleaned up): start fresh from the transcript.
            return await RunTurnAsync(request, BuildTranscriptPrompt(request, includeInstructions: false), null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ModelResponse> RunTurnAsync(MessageRequest request, string prompt, string? resume, CancellationToken cancellationToken)
    {
        List<string> args = ["-p", "--output-format", "stream-json", "--verbose", "--append-system-prompt", Instructions];
        if (SubModel(request.Model) is { } model)
        {
            args.AddRange(["--model", model]);
        }

        if (resume is not null)
        {
            args.AddRange(["--resume", resume]);
        }

        args.AddRange(Options.AutoApprove ? ["--dangerously-skip-permissions"] : ["--permission-mode", "acceptEdits"]);
        args.AddRange(ExtraArguments(Environment.GetEnvironmentVariable("SHUNCHAKI_CLAUDE_CODE_ARGS")));

        JsonObject? result = null;
        var run = await RunAsync(args, prompt, line =>
        {
            if (!line.StartsWith('{'))
            {
                return;
            }

            JsonObject? evt;
            try
            {
                evt = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                return;
            }

            switch (evt?["type"]?.GetValue<string>())
            {
                case "result":
                    result = evt;
                    break;
                case "assistant":
                    foreach (var block in (evt["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    {
                        if (block["type"]?.GetValue<string>() == "tool_use")
                        {
                            request.Activity?.Invoke($"{block["name"]?.GetValue<string>()}: {SummarizeInput(block["input"])}");
                        }
                    }

                    break;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (run.TimedOut)
        {
            throw Failure($"Claude Code did not finish within {Options.CliTimeout.TotalMinutes:0} min", 504);
        }

        if (result is null)
        {
            throw Failure(run.StandardError.Length > 0 ? run.StandardError : run.StandardOutput);
        }

        var text = result["result"]?.GetValue<string>() ?? string.Empty;
        if (result["is_error"]?.GetValue<bool>() == true || result["subtype"]?.GetValue<string>() is { } subtype && subtype != "success")
        {
            int? status = result["api_error_status"] is JsonValue s && s.TryGetValue<int>(out var code) ? code : null;
            throw Failure(text.Length > 0 ? text : result["subtype"]?.GetValue<string>() ?? "error", status);
        }

        if (result["permission_denials"] is JsonArray { Count: > 0 } denials)
        {
            request.Activity?.Invoke($"⚠ {denials.Count} action(s) were denied by Claude Code's permission mode (run shunchaki with --yes to allow them)");
        }

        var usage = result["usage"];
        return new ModelResponse("end_turn", ResultContent(text, result["session_id"]?.GetValue<string>()), null, new TokenUsage(
            Long(usage, "input_tokens"), Long(usage, "output_tokens"),
            Long(usage, "cache_read_input_tokens"), Long(usage, "cache_creation_input_tokens")));
    }
}

/// <summary>
/// Gemini CLI (<c>gemini -p --output-format json</c>) using the user's Google account login.
/// Gemini CLI cannot resume a session by id headlessly, so every turn gets the shared transcript.
/// </summary>
public sealed class GeminiCliProvider(string executable, AgentOptions options) : CliAgentProvider(executable, options)
{
    public override string Name => ModelProviders.GeminiCli;

    public override async Task<ModelResponse> CreateMessageAsync(MessageRequest request, int maxRetries, CancellationToken cancellationToken)
    {
        // The prompt arrives on stdin; -p's text is appended after it.
        List<string> args = ["-p", "Carry out the request above.", "--output-format", "json",
            "--approval-mode", Options.AutoApprove ? "yolo" : "auto_edit"];
        if (SubModel(request.Model) is { } model)
        {
            args.AddRange(["--model", model]);
        }

        args.AddRange(ExtraArguments(Environment.GetEnvironmentVariable("SHUNCHAKI_GEMINI_CLI_ARGS")));

        var run = await RunAsync(args, BuildTranscriptPrompt(request, includeInstructions: true), null, cancellationToken).ConfigureAwait(false);
        if (run.TimedOut)
        {
            throw Failure($"Gemini CLI did not finish within {Options.CliTimeout.TotalMinutes:0} min", 504);
        }

        var json = ParseJson(run.StandardOutput) ?? ParseJson(run.StandardError);
        if (json?["error"] is JsonObject error)
        {
            int? status = error["code"] is JsonValue c && c.TryGetValue<int>(out var code) && code >= 400 ? code : null;
            throw Failure(error["message"]?.GetValue<string>() ?? error.ToJsonString(), status);
        }

        if (json is null)
        {
            if (run.ExitCode != 0)
            {
                throw Failure(run.StandardError.Length > 0 ? run.StandardError : run.StandardOutput);
            }

            // Older Gemini CLI without JSON output: the answer is plain text.
            return new ModelResponse("end_turn", ResultContent(run.StandardOutput.Trim(), null), null, default);
        }

        ReportTools(json["stats"]?["tools"], request.Activity);
        return new ModelResponse("end_turn", ResultContent(json["response"]?.GetValue<string>() ?? string.Empty, null), null,
            Usage(json["stats"]?["models"] as JsonObject));
    }

    private static void ReportTools(JsonNode? tools, Action<string>? activity)
    {
        if (activity is null || tools?["byName"] is not JsonObject byName)
        {
            return;
        }

        foreach (var (name, stats) in byName)
        {
            var count = Long(stats, "count");
            if (count > 0)
            {
                activity($"{name} ×{count}");
            }
        }
    }

    private static TokenUsage Usage(JsonObject? models)
    {
        var usage = default(TokenUsage);
        foreach (var (_, model) in models ?? [])
        {
            var tokens = model?["tokens"];
            var cached = Long(tokens, "cached");
            usage += new TokenUsage(Long(tokens, "prompt") - cached, Long(tokens, "candidates") + Long(tokens, "thoughts"), cached, 0);
        }

        return usage;
    }

    private static JsonObject? ParseJson(string text)
    {
        // The JSON document may follow log lines; start at the first '{' at a line start.
        var start = text.StartsWith('{') ? 0 : text.IndexOf("\n{", StringComparison.Ordinal) + 1;
        if (start < 0 || start >= text.Length || text[start] != '{')
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text[start..]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
