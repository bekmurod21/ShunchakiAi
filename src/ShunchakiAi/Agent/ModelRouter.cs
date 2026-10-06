using ShunchakiAi.Api;

namespace ShunchakiAi.Agent;

public sealed record ModelStatus(string Model, string State);

/// <summary>
/// Ordered failover chain. Each request goes to the first model that is not cooling down
/// (rate limit, overload) or excluded (not enabled, context too small, refused this turn),
/// so the work moves down the chain when a model runs out and returns to the preferred model
/// once it recovers.
/// </summary>
public sealed class ModelRouter(IReadOnlyList<string> models)
{
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _excluded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skippedThisTurn = new(StringComparer.Ordinal);
    private readonly HashSet<string> _providerWide = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Models => models;

    /// <summary>
    /// Picks the model for the next request. Returns <c>Wait &gt; 0</c> when every usable model
    /// is cooling down, and <c>Model == null</c> when none can serve this conversation.
    /// </summary>
    public (string? Model, TimeSpan Wait) Select(DateTimeOffset now)
    {
        var usable = models.Where(m => !_excluded.ContainsKey(m) && !_skippedThisTurn.Contains(m)).ToList();
        if (usable.Count == 0)
        {
            return (null, TimeSpan.Zero);
        }

        foreach (var model in usable)
        {
            if (!_cooldownUntil.TryGetValue(model, out var until) || until <= now)
            {
                return (model, TimeSpan.Zero);
            }
        }

        var soonest = usable.MinBy(m => _cooldownUntil[m])!;
        return (soonest, _cooldownUntil[soonest] - now);
    }

    /// <summary>True if another model could take over right now if <paramref name="model"/> fails.</summary>
    public bool HasAlternative(string model, DateTimeOffset now) =>
        models.Any(m => m != model && !_excluded.ContainsKey(m) && !_skippedThisTurn.Contains(m)
                        && (!_cooldownUntil.TryGetValue(m, out var until) || until <= now));

    public void ReportFailure(string model, Failure failure, DateTimeOffset now)
    {
        if (failure.Scope == FailureScope.Provider)
        {
            _providerWide.Add(failure.Reason);
            var provider = ModelProviders.ProviderOf(model);
            foreach (var other in models.Where(m => ModelProviders.ProviderOf(m) == provider))
            {
                _excluded[other] = failure.Reason;
            }
        }
        else if (failure.Scope == FailureScope.Conversation)
        {
            _excluded[model] = failure.Reason;
        }
        else
        {
            _cooldownUntil[model] = now + failure.Cooldown;
        }
    }

    public void ReportSuccess(string model) => _cooldownUntil.Remove(model);

    /// <summary>Skip a model for the rest of the current user turn (e.g. it declined the request).</summary>
    public void SkipForTurn(string model) => _skippedThisTurn.Add(model);

    public void BeginTurn() => _skippedThisTurn.Clear();

    /// <summary>A new conversation may fit models that were excluded for the old one.</summary>
    public void ResetConversation()
    {
        // Provider-wide problems (bad key, no credit) do not go away with a new conversation.
        foreach (var model in _excluded.Where(e => !_providerWide.Contains(e.Value)).Select(e => e.Key).ToList())
        {
            _excluded.Remove(model);
        }

        _skippedThisTurn.Clear();
    }

    public IEnumerable<ModelStatus> Describe(DateTimeOffset now) => models.Select(m => new ModelStatus(m,
        _excluded.TryGetValue(m, out var why) ? $"excluded ({why})"
        : _skippedThisTurn.Contains(m) ? "skipped this turn"
        : _cooldownUntil.TryGetValue(m, out var until) && until > now ? $"cooling down {(until - now).TotalSeconds:0}s"
        : "ready"));
}
