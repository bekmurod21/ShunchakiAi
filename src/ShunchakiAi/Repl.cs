using ShunchakiAi.Agent;
using ShunchakiAi.Sessions;
using ShunchakiAi.Ui;

namespace ShunchakiAi;

/// <summary>Read-Eval-Print Loop: read a request, run the agent turn, repeat.</summary>
public sealed class Repl(AgentSession session, SessionStore store, LoginFlow login, ConsoleRenderer ui, TurnCancellation cancellation)
{
    public async Task<int> RunAsync()
    {
        while (true)
        {
            var input = ui.ReadUserInput();
            if (input is null)
            {
                ui.ShowInfo("Bye!");
                return 0;
            }

            input = input.Trim();
            if (input.Length == 0)
            {
                continue;
            }

            if (input.StartsWith('/'))
            {
                if (!await HandleCommandAsync(input))
                {
                    return 0;
                }

                continue;
            }

            var token = cancellation.BeginTurn();
            try
            {
                await session.RunTurnAsync(input, token);
            }
            finally
            {
                cancellation.EndTurn();
            }
        }
    }

    /// <summary>Returns false when the REPL should exit.</summary>
    private async Task<bool> HandleCommandAsync(string command)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/exit" or "/quit":
                ui.ShowInfo($"Bye! Session saved: {session.SessionId}");
                return false;
            case "/clear":
                session.Reset();
                ui.ShowInfo($"New conversation. Session: {session.SessionId}");
                return true;
            case "/usage":
                var u = session.TotalUsage;
                ui.ShowInfo($"Session: {u.InputTokens:N0} input, {u.CacheReadTokens:N0} cache-read, " +
                            $"{u.CacheWriteTokens:N0} cache-write, {u.OutputTokens:N0} output tokens.");
                foreach (var (model, usage) in session.UsageByModel)
                {
                    ui.ShowInfo($"  {model}: {usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens:N0} in, {usage.OutputTokens:N0} out");
                }

                return true;
            case "/models":
                foreach (var status in session.Router.Describe(DateTimeOffset.Now))
                {
                    var marker = status.Model == session.ActiveModel ? "▶" : " ";
                    ui.ShowInfo($"{marker} {status.Model}: {status.State}");
                }

                return true;
            case "/sessions":
                var sessions = store.List();
                if (sessions.Count == 0)
                {
                    ui.ShowInfo("No saved sessions yet.");
                }

                foreach (var info in sessions)
                {
                    var current = info.Id == session.SessionId ? " (current)" : string.Empty;
                    ui.ShowInfo($"{info.Id}  {info.Updated:yyyy-MM-dd HH:mm}{current}  {info.FirstRequest}");
                }

                return true;
            case "/resume":
                var id = parts.Length > 1 ? parts[1] : store.List(2).FirstOrDefault(s => s.Id != session.SessionId)?.Id;
                if (id is null)
                {
                    ui.ShowWarning("No other saved session to resume.");
                    return true;
                }

                SessionLoader.TryResume(session, store, ui, id);
                return true;
            case "/login" or "/logout":
                string? provider;
                try
                {
                    provider = LoginFlow.ParseProvider(parts.Length > 1 ? parts[1] : null);
                }
                catch (ArgumentException ex)
                {
                    ui.ShowWarning(ex.Message);
                    return true;
                }

                if (parts[0].Equals("/login", StringComparison.OrdinalIgnoreCase))
                {
                    await login.LoginAsync(provider);
                }
                else
                {
                    login.Logout(provider);
                }

                return true;
            case "/keys":
                login.ShowKeys();
                return true;
            case "/help":
                ui.ShowReplHelp();
                return true;
            default:
                ui.ShowWarning($"Unknown command '{command}'. Type /help.");
                return true;
        }
    }
}

public static class SessionLoader
{
    public static bool TryResume(AgentSession session, SessionStore store, ConsoleRenderer ui, string id)
    {
        try
        {
            var snapshot = store.Load(id);
            session.Load(snapshot);
            ui.ShowInfo($"Resumed session {snapshot.Id} ({snapshot.Messages.Count} messages, {snapshot.WorkLog.Count} work-log entries" +
                        (snapshot.LastModel is null ? ")." : $", last model {snapshot.LastModel})."));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {
            ui.ShowError($"Could not resume session '{id}': {ex.Message}");
            return false;
        }
    }
}
