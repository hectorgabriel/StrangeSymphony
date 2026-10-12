using System.Text;

namespace Symphony.Claude;

/// <summary>Newline-delimited reader with a per-line cap (SPEC 10.1 recommends 10 MB).</summary>
public sealed class BoundedLineReader(TextReader reader, int maxLineChars)
{
    private readonly char[] _buffer = new char[16 * 1024];
    private int _pos;
    private int _len;

    /// <summary>Returns the next line, <c>(null, false)</c> at EOF, or <c>(null, true)</c> for an oversized (discarded) line.</summary>
    public async Task<(string? Line, bool Oversized)> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var oversized = false;
        while (true)
        {
            if (_pos == _len)
            {
                _len = await reader.ReadAsync(_buffer.AsMemory(), ct);
                _pos = 0;
                if (_len == 0)
                {
                    if (oversized) return (null, true);
                    return sb.Length == 0 ? (null, false) : (sb.ToString(), false);
                }
            }

            var span = _buffer.AsSpan(_pos, _len - _pos);
            var nl = span.IndexOf('\n');
            var chunk = nl >= 0 ? span[..nl] : span;
            if (!oversized)
            {
                if (sb.Length + chunk.Length > maxLineChars)
                {
                    oversized = true;
                    sb.Clear();
                }
                else
                {
                    sb.Append(chunk);
                }
            }

            _pos += nl >= 0 ? nl + 1 : chunk.Length;
            if (nl >= 0)
            {
                if (oversized) return (null, true);
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return (sb.ToString(), false);
            }
        }
    }
}
