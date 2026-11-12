using System.Text.RegularExpressions;

namespace AppMover.Core.Text;

/// <summary>
/// Replaces machine/user specific folders by tokens (<c>C:\Program Files\App</c> → <c>{ProgramFiles}\App</c>)
/// so that a package can be deployed on another machine, for another user or in another directory.
/// </summary>
public sealed class PathTokenizer
{
    /// <summary>Token of the application install directory (allows relocation).</summary>
    public const string InstallDirToken = "InstallDir";

    /// <summary>Token suffix of a path written with doubled backslashes (JSON, C strings): <c>{ProgramFiles:json}</c>.</summary>
    public const string JsonStyle = "json";
    /// <summary>Token suffix of a path written with forward slashes: <c>{ProgramFiles:slash}</c>.</summary>
    public const string SlashStyle = "slash";

    private readonly List<(string Token, string Path)> _mappings;
    /// <summary>Folder as written in a text (3 styles) → its token.</summary>
    private readonly Dictionary<string, string> _tokenByText = new(StringComparer.OrdinalIgnoreCase);
    private readonly Regex? _textPattern;

    /// <param name="mappings">Token name (without braces) → absolute path. The longest path wins.</param>
    public PathTokenizer(IEnumerable<(string Token, string Path)> mappings)
    {
        _mappings = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.Path))
            .Select(m => (m.Token, Path: WinPath.Normalize(m.Path)))
            .Where(m => !WinPath.IsRoot(m.Path))
            .DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(m => m.Path.Length)
            .ToList();
        foreach (var (token, path) in _mappings)
        {
            _tokenByText.TryAdd(path, "{" + token + "}");
            _tokenByText.TryAdd(path.Replace(@"\", @"\\"), "{" + token + ":" + JsonStyle + "}");
            _tokenByText.TryAdd(path.Replace('\\', '/'), "{" + token + ":" + SlashStyle + "}");
        }

        if (_tokenByText.Count > 0)
        {
            // A folder is only replaced when followed by a separator or a delimiter (not "C:\App" in "C:\Apps").
            var alternatives = string.Join("|", _tokenByText.Keys.OrderByDescending(t => t.Length).Select(Regex.Escape));
            _textPattern = new Regex($@"(?:{alternatives})(?=$|[\\/""';,\s|<>=)\]])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    public IReadOnlyList<(string Token, string Path)> Mappings => _mappings;

    /// <summary>Tokenizes a path starting with a known folder; other paths are returned normalized.</summary>
    public string Tokenize(string path)
    {
        var normalized = WinPath.Normalize(path);
        foreach (var (token, root) in _mappings)
        {
            if (WinPath.IsUnderOrEqual(normalized, root))
                return "{" + token + "}" + normalized[root.Length..];
        }
        return normalized;
    }

    /// <summary>
    /// Tokenizes every known folder found in a text (registry value, command line, configuration file…), also when written
    /// with doubled backslashes (<c>C:\\Program Files</c> → <c>{ProgramFiles:json}</c>) or forward slashes (<c>{ProgramFiles:slash}</c>).
    /// </summary>
    public string TokenizeText(string text) =>
        _textPattern is null ? text : _textPattern.Replace(text, m => _tokenByText[m.Value]);

    /// <summary>
    /// Entry name inside the archive for a tokenized path:
    /// <c>{ProgramFiles}\App\a.exe</c> → <c>files/ProgramFiles/App/a.exe</c>, <c>D:\Games\x</c> → <c>files/_drive_D/Games/x</c>.
    /// </summary>
    public static string ToArchivePath(string tokenizedPath)
    {
        string relative;
        if (tokenizedPath.StartsWith('{') && tokenizedPath.IndexOf('}') is var end and > 0)
            relative = tokenizedPath[1..end] + tokenizedPath[(end + 1)..];
        else if (tokenizedPath.Length >= 2 && tokenizedPath[1] == ':')
            relative = "_drive_" + char.ToUpperInvariant(tokenizedPath[0]) + tokenizedPath[2..];
        else if (tokenizedPath.StartsWith(@"\\", StringComparison.Ordinal))
            relative = "_unc\\" + tokenizedPath[2..];
        else
            relative = "_other\\" + tokenizedPath;

        return "files/" + relative.Replace('\\', '/').TrimEnd('/');
    }
}
