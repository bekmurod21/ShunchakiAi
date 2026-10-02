using ShunchakiAi.Agent;
using ShunchakiAi.Ui;

namespace ShunchakiAi;

/// <summary>Read-Eval-Print Loop: read a request, run the agent turn, repeat.</summary>
public sealed class Repl(AgentSession session, ConsoleRenderer ui, TurnCancellation cancellation)
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
                if (!HandleCommand(input))
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
    private bool HandleCommand(string command)
    {
        switch (command.Split(' ', 2)[0].ToLowerInvariant())
        {
            case "/exit" or "/quit":
                ui.ShowInfo("Bye!");
                return false;
            case "/clear":
                session.Reset();
                ui.ShowInfo("Conversation cleared.");
                return true;
            case "/usage":
                var u = session.TotalUsage;
                ui.ShowInfo($"Session: {u.InputTokens:N0} input, {u.CacheReadTokens:N0} cache-read, " +
                            $"{u.CacheWriteTokens:N0} cache-write, {u.OutputTokens:N0} output tokens.");
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
