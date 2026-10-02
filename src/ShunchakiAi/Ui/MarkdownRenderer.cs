using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ShunchakiAi.Ui;

/// <summary>
/// Renders the subset of Markdown that models actually produce - headings, lists, quotes,
/// rules, fenced code blocks and inline code/bold/italic/links - as Spectre renderables.
/// All model text is escaped before markup is applied, so stray brackets can't break output.
/// </summary>
public static partial class MarkdownRenderer
{
    public static IEnumerable<IRenderable> Render(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                foreach (var item in FlushParagraph(paragraph))
                {
                    yield return item;
                }

                var language = trimmed[3..].Trim();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Append(lines[i]).Append('\n');
                }

                yield return CodeBlock(language, code.ToString().TrimEnd('\n'));
                continue;
            }

            if (trimmed.Length == 0)
            {
                foreach (var item in FlushParagraph(paragraph))
                {
                    yield return item;
                }

                continue;
            }

            IRenderable? block = null;
            if (HeadingPattern().Match(trimmed) is { Success: true } heading)
            {
                var level = heading.Groups[1].Length;
                var style = level == 1 ? "bold underline deepskyblue1" : level == 2 ? "bold deepskyblue1" : "bold";
                block = new Markup($"[{style}]{Inline(heading.Groups[2].Value)}[/]\n");
            }
            else if (RulePattern().IsMatch(trimmed))
            {
                block = new Rule().RuleStyle("grey");
            }
            else if (BulletPattern().Match(line) is { Success: true } bullet)
            {
                var indent = new string(' ', bullet.Groups[1].Length);
                block = new Markup($"{indent} [deepskyblue1]•[/] {Inline(bullet.Groups[2].Value)}\n");
            }
            else if (NumberedPattern().Match(line) is { Success: true } numbered)
            {
                var indent = new string(' ', numbered.Groups[1].Length);
                block = new Markup($"{indent} [deepskyblue1]{numbered.Groups[2].Value}.[/] {Inline(numbered.Groups[3].Value)}\n");
            }
            else if (trimmed.StartsWith('>'))
            {
                block = new Markup($"[grey]│[/] [italic]{Inline(trimmed.TrimStart('>').TrimStart())}[/]\n");
            }
            else if (trimmed.StartsWith('|'))
            {
                // Tables: keep alignment by rendering the raw row in a monospace-friendly way.
                block = new Markup($"[grey]{Markup.Escape(line)}[/]\n");
            }

            if (block is null)
            {
                paragraph.Append(paragraph.Length > 0 ? " " : string.Empty).Append(trimmed);
                continue;
            }

            foreach (var item in FlushParagraph(paragraph))
            {
                yield return item;
            }

            yield return block;
        }

        foreach (var item in FlushParagraph(paragraph))
        {
            yield return item;
        }
    }

    private static IEnumerable<IRenderable> FlushParagraph(StringBuilder paragraph)
    {
        if (paragraph.Length == 0)
        {
            yield break;
        }

        var text = paragraph.ToString();
        paragraph.Clear();
        yield return new Markup(Inline(text) + "\n");
    }

    private static IRenderable CodeBlock(string language, string code)
    {
        var panel = new Panel(new Text(code, new Style(Color.Grey93)))
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Grey35))
            .Expand();

        if (language.Length > 0)
        {
            panel.Header($"[grey] {Markup.Escape(language)} [/]");
        }

        return panel;
    }

    /// <summary>Applies inline styles; every literal segment is escaped.</summary>
    private static string Inline(string text)
    {
        var result = new StringBuilder(text.Length + 32);
        var last = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            result.Append(Markup.Escape(text[last..match.Index]));
            last = match.Index + match.Length;

            if (match.Groups["code"].Success)
            {
                result.Append("[orange1 on grey15]").Append(Markup.Escape(match.Groups["code"].Value)).Append("[/]");
            }
            else if (match.Groups["bold"].Success)
            {
                result.Append("[bold]").Append(Markup.Escape(match.Groups["bold"].Value)).Append("[/]");
            }
            else if (match.Groups["italic"].Success)
            {
                result.Append("[italic]").Append(Markup.Escape(match.Groups["italic"].Value)).Append("[/]");
            }
            else if (match.Groups["label"].Success)
            {
                result.Append("[underline deepskyblue1]").Append(Markup.Escape(match.Groups["label"].Value)).Append("[/]")
                      .Append(" [grey](").Append(Markup.Escape(match.Groups["url"].Value)).Append(")[/]");
            }
        }

        result.Append(Markup.Escape(text[last..]));
        return result.ToString();
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^(\s*)(\d+)[.)]\s+(.*)$")]
    private static partial Regex NumberedPattern();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|__(?<bold>[^_]+)__|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex InlinePattern();
}
