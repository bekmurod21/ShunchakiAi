using System.Text;

namespace ShunchakiAi;

/// <summary>Reads piped stdin without hanging on an inherited-but-idle pipe.</summary>
public static class StandardInput
{
    private static readonly TimeSpan FirstDataGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When the prompt already came from the command line, stdin is optional context: scripts
    /// and CI often leave an idle pipe attached that never reaches EOF, so give up if nothing
    /// arrives within a short grace period. Without a prompt, stdin is the prompt: wait for it.
    /// </summary>
    public static async Task<string> ReadAsync(bool waitForFirstData)
    {
        var reader = Console.In;
        var buffer = new char[4096];
        // Console.In is a synchronized reader whose ReadAsync blocks the caller,
        // so the first read runs on a worker thread to make the grace period effective.
        var firstRead = Task.Run(() => reader.Read(buffer, 0, buffer.Length));

        if (!waitForFirstData && await Task.WhenAny(firstRead, Task.Delay(FirstDataGrace)) != firstRead)
        {
            return string.Empty;
        }

        var count = await firstRead;
        if (count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder().Append(buffer, 0, count);
        text.Append(await Task.Run(reader.ReadToEnd));
        return text.ToString();
    }
}
