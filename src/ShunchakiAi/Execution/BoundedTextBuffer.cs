using System.Text;

namespace ShunchakiAi.Execution;

/// <summary>
/// Collects text up to a fixed memory budget: keeps the first <c>headChars</c> and the last
/// <c>tailChars</c> characters and counts everything dropped in between. A chatty command
/// (e.g. <c>yes</c> or a verbose build) can therefore never grow memory without bound, and
/// the tail - where errors usually are - is still preserved.
/// </summary>
public sealed class BoundedTextBuffer(int headChars, int tailChars)
{
    private readonly StringBuilder _head = new(Math.Min(headChars, 4096));
    private readonly char[] _tail = new char[tailChars];
    private int _tailStart;
    private int _tailLength;
    private long _dropped;

    public void Append(ReadOnlySpan<char> text)
    {
        var headRoom = headChars - _head.Length;
        if (headRoom > 0)
        {
            var take = Math.Min(headRoom, text.Length);
            _head.Append(text[..take]);
            text = text[take..];
        }

        foreach (var c in text)
        {
            if (_tail.Length == 0)
            {
                _dropped++;
                continue;
            }

            if (_tailLength < _tail.Length)
            {
                _tail[(_tailStart + _tailLength++) % _tail.Length] = c;
            }
            else
            {
                // Ring buffer full: overwrite the oldest character.
                _tail[_tailStart] = c;
                _tailStart = (_tailStart + 1) % _tail.Length;
                _dropped++;
            }
        }
    }

    public override string ToString()
    {
        var result = new StringBuilder(_head.Length + _tailLength + 64);
        result.Append(_head);
        if (_dropped > 0)
        {
            result.Append($"\n... [{_dropped:N0} characters omitted] ...\n");
        }

        for (var i = 0; i < _tailLength; i++)
        {
            result.Append(_tail[(_tailStart + i) % _tail.Length]);
        }

        return result.ToString();
    }
}
