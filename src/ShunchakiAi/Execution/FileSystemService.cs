using System.Text;

namespace ShunchakiAi.Execution;

/// <summary>Raised when a path resolves outside the workspace root.</summary>
public sealed class WorkspaceAccessException(string message) : Exception(message);

/// <summary>
/// File operations confined to a workspace root. Every path the model supplies is
/// resolved to an absolute path and rejected if it escapes the root (<c>../</c>,
/// absolute paths elsewhere, symlinked directories pointing outside).
/// </summary>
public sealed class FileSystemService
{
    public const long MaxReadBytes = 512 * 1024;
    private const int MaxListEntries = 500;
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs", ".idea" };

    private readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public FileSystemService(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.");
        }

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        EnsureInsideRoot(full);

        // Every existing component below the root must not be a symlink pointing outside it.
        for (var probe = full;
             !string.IsNullOrEmpty(probe) && !Path.TrimEndingDirectorySeparator(probe).Equals(Root, _pathComparison);
             probe = Path.GetDirectoryName(probe))
        {
            FileSystemInfo info = Directory.Exists(probe) ? new DirectoryInfo(probe) : new FileInfo(probe);
            if (info.Exists && info.LinkTarget is not null
                && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                EnsureInsideRoot(Path.GetFullPath(target.FullName));
            }
        }

        return full;
    }

    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath);

    public string ReadText(string path, int startLine, int maxLines)
    {
        var full = Resolve(path);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"File not found: {Relative(full)}");
        }

        if (info.Length > MaxReadBytes && startLine <= 1 && maxLines <= 0)
        {
            maxLines = 2000;
        }

        var output = new StringBuilder();
        var lineNumber = 0;
        var emitted = 0;
        using var reader = new StreamReader(full, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (lineNumber < startLine)
            {
                continue;
            }

            if (maxLines > 0 && emitted >= maxLines)
            {
                output.AppendLine($"... [truncated; file continues past line {lineNumber - 1}. Use start_line to read more]");
                break;
            }

            if (line.Contains('\0'))
            {
                throw new InvalidDataException($"{Relative(full)} looks like a binary file.");
            }

            output.Append(lineNumber.ToString().PadLeft(6)).Append('\t').AppendLine(line);
            emitted++;
        }

        return emitted == 0 ? $"(no content at or after line {startLine}; file has {lineNumber} lines)" : output.ToString();
    }

    /// <summary>Writes via a temp file + atomic move so a crash never leaves a half-written file.</summary>
    public string WriteText(string path, string content)
    {
        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            throw new IOException($"{Relative(full)} is a directory.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".shunchaki.tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return full;
    }

    public string ReplaceText(string path, string oldText, string newText)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"File not found: {Relative(full)}");
        }

        var content = File.ReadAllText(full);
        var first = content.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
        {
            throw new InvalidOperationException("old_string was not found in the file. Read the file again and copy the text exactly.");
        }

        if (content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("old_string matches more than once. Include more surrounding context to make it unique.");
        }

        WriteText(path, string.Concat(content.AsSpan(0, first), newText, content.AsSpan(first + oldText.Length)));
        return full;
    }

    public string ListDirectory(string path)
    {
        var full = Resolve(path);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Directory not found: {Relative(full)}");
        }

        var output = new StringBuilder();
        var count = 0;
        var directory = new DirectoryInfo(full);
        foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (++count > MaxListEntries)
            {
                output.AppendLine($"... [more than {MaxListEntries} entries; list a subdirectory]");
                break;
            }

            output.AppendLine(entry switch
            {
                DirectoryInfo d when SkippedDirectories.Contains(d.Name) => $"{d.Name}/  (contents hidden)",
                DirectoryInfo d => $"{d.Name}/",
                FileInfo f => $"{f.Name}  ({FormatSize(f.Length)})",
                _ => entry.Name,
            });
        }

        return count == 0 ? "(empty directory)" : output.ToString();
    }

    private void EnsureInsideRoot(string fullPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        var inside = trimmed.Equals(Root, _pathComparison)
            || trimmed.StartsWith(Root + Path.DirectorySeparatorChar, _pathComparison);
        if (!inside)
        {
            throw new WorkspaceAccessException($"Access denied: '{fullPath}' is outside the workspace '{Root}'.");
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
