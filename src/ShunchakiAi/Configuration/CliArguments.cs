namespace ShunchakiAi.Configuration;

/// <summary>
/// Parsed command-line arguments. Everything that is not a recognised flag is
/// joined into the single-shot prompt, so <c>ai refactor this file</c> and
/// <c>ai "refactor this file"</c> behave the same.
/// </summary>
public sealed record CliArguments
{
    public string? Prompt { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool AutoApprove { get; init; }
    public bool Resume { get; init; }
    public string? SessionId { get; init; }
    public bool NoFailover { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var result = new CliArguments();
        // -m accepts a comma-separated failover chain: -m claude-opus-5-5,claude-sonnet-5-5
        var promptParts = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    result = result with { ShowHelp = true };
                    break;
                case "-v" or "--version":
                    result = result with { ShowVersion = true };
                    break;
                case "-y" or "--yes":
                    result = result with { AutoApprove = true };
                    break;
                case "-m" or "--model":
                    result = result with { Model = RequireValue(args, ref i, arg) };
                    break;
                case "-r" or "--resume":
                    result = result with { Resume = true };
                    break;
                case "-s" or "--session":
                    result = result with { SessionId = RequireValue(args, ref i, arg), Resume = true };
                    break;
                case "--no-failover":
                    result = result with { NoFailover = true };
                    break;
                case "-e" or "--effort":
                    result = result with { Effort = RequireValue(args, ref i, arg) };
                    break;
                case "-C" or "--cwd":
                    result = result with { WorkingDirectory = RequireValue(args, ref i, arg) };
                    break;
                case "--":
                    promptParts.AddRange(args.Skip(i + 1));
                    i = args.Count;
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1 && promptParts.Count == 0)
                    {
                        throw new ConfigurationException($"Unknown option '{arg}'. Run 'ai --help' for usage.");
                    }
                    promptParts.Add(arg);
                    break;
            }
        }

        var prompt = string.Join(' ', promptParts).Trim();
        return result with { Prompt = prompt.Length == 0 ? null : prompt };
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ConfigurationException($"Option '{option}' requires a value.");
        }

        return args[++index];
    }
}
