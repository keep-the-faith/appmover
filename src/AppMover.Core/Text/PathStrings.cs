using System.Text;
using System.Text.RegularExpressions;

namespace AppMover.Core.Text;

/// <summary>Finds absolute Windows paths in any data (installer logs in an undocumented binary format, text logs…).</summary>
public static partial class PathStrings
{
    /// <summary>Distinct paths found as ASCII/ANSI or UTF-16 text (both byte alignments).</summary>
    public static IReadOnlyList<string> Extract(byte[] data)
    {
        var texts = new[]
        {
            Encoding.Latin1.GetString(data),
            Encoding.Unicode.GetString(data, 0, data.Length & ~1),
            data.Length > 1 ? Encoding.Unicode.GetString(data, 1, (data.Length - 1) & ~1) : "",
        };
        return texts
            .SelectMany(t => AbsolutePath().Matches(t).Select(m => m.Value.TrimEnd(' ', '.', '\\')))
            .Where(p => p.Length > 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [GeneratedRegex(@"(?<![A-Za-z])[A-Za-z]:\\[^\x00-\x1F""*?<>|�]{2,259}")]
    private static partial Regex AbsolutePath();
}
