using System.Text.RegularExpressions;

namespace AppMover.Core.Text;

/// <summary>
/// Expands path tokens (<c>{ProgramFiles}</c>, <c>{U0.AppData}</c>, <c>{InstallDir}</c>…) into the folders of a machine:
/// the inverse of <see cref="PathTokenizer"/>.
/// </summary>
public sealed partial class TokenMap(IReadOnlyDictionary<string, string> folders)
{
    private readonly Dictionary<string, string> _folders = folders.ToDictionary(
        f => f.Key, f => WinPath.Normalize(f.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>Token (without braces) → folder.</summary>
    public IReadOnlyDictionary<string, string> Folders => _folders;

    /// <summary>
    /// Expands a tokenized path (token at the start only). Untokenized paths are returned normalized.
    /// Returns null when the token is unknown (e.g. a source user that is not mapped).
    /// </summary>
    public string? ExpandPath(string tokenizedPath)
    {
        if (!tokenizedPath.StartsWith('{'))
            return WinPath.Normalize(tokenizedPath);
        var end = tokenizedPath.IndexOf('}');
        if (end < 0 || !_folders.TryGetValue(tokenizedPath[1..end], out var folder))
            return null;
        return WinPath.Normalize(folder + tokenizedPath[(end + 1)..]);
    }

    /// <summary>Expands the known tokens of a text, in their style (json, slash); unknown ones are left untouched.</summary>
    public string ExpandText(string text) =>
        Token().Replace(text, m =>
        {
            if (!_folders.TryGetValue(m.Groups[1].Value, out var folder))
                return m.Value;
            return m.Groups[2].Value switch
            {
                PathTokenizer.JsonStyle => folder.Replace(@"\", @"\\"),
                PathTokenizer.SlashStyle => folder.Replace('\\', '/'),
                _ => folder,
            };
        });

    // GUIDs ("{1111-…}") are not tokens: they contain dashes.
    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_.]*)(?::(json|slash))?\}")]
    private static partial Regex Token();
}
