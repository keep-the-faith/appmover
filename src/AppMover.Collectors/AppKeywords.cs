using System.Text;
using System.Text.RegularExpressions;
using AppMover.Core.Models;
using AppMover.Core.Text;

namespace AppMover.Collectors;

public enum KeywordMatch { None, Partial, Publisher, ProductAlias, Product }

/// <summary>Names used to recognise the application's data folders and registry keys.</summary>
public sealed partial class AppKeywords
{
    /// <summary>Folder/key names too common to be attributed to one application.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "packages", "temp", "tmp", "programs", "program", "cache", "caches", "crashdumps", "common",
        "shared", "data", "user", "users", "application", "applications", "app", "apps", "software", "settings", "config",
        "local", "roaming", "locallow", "classes", "policies", "wow6432node", "clients", "google", "apple", "intel", "nvidia",
        "amd", "oracle", "logs", "log", "default", "system", "tools", "setup", "installer", "update", "updates", "runtime",
        "driver", "drivers", "documents", "desktop", "downloads", "files", "the", "team", "inc", "free", "open", "client",
        "server", "service", "services", "plugins", "plugin", "registeredapplications", "odbc", "mozillaplugins",
    };

    /// <summary>Publishers with too many unrelated products to claim their folder from the publisher name alone.</summary>
    private static readonly HashSet<string> GenericPublishers = new(StringComparer.Ordinal)
    {
        "microsoft", "google", "apple", "intel", "nvidia", "amd", "oracle", "adobe", "hp", "dell", "lenovo",
    };

    private static readonly string[] CompanySuffixes =
    [
        "incorporated", "inc", "corporation", "corp", "limited", "ltd", "gmbh", "llc", "sa", "sas", "sarl", "ag", "bv", "co",
        "company", "team", "software", "technologies", "technology", "foundation", "project", "contributors", "developers",
        "group", "labs", "studio", "studios", "systems",
    ];

    public static AppKeywords Empty { get; } = new([], [], []);

    private AppKeywords(IReadOnlyList<string> products, IReadOnlyList<string> aliases, IReadOnlyList<string> publishers)
    {
        Products = products;
        Aliases = aliases;
        Publishers = publishers;
    }

    /// <summary>Normalized product names (strong).</summary>
    public IReadOnlyList<string> Products { get; }
    /// <summary>Normalized weaker product names (first word of the name…).</summary>
    public IReadOnlyList<string> Aliases { get; }
    /// <summary>Normalized publisher names.</summary>
    public IReadOnlyList<string> Publishers { get; }

    /// <summary>True when the publisher is specific enough for its folder to be proposed (as "Possible").</summary>
    public bool IsSpecificPublisher(string normalized) => !GenericPublishers.Contains(normalized);

    public static AppKeywords From(InstalledApp app, IEnumerable<string> installRoots, Func<string, bool> isKnownFolder)
    {
        var products = new List<string>();
        var aliases = new List<string>();
        var publishers = new List<string>();

        var publisher = CleanPublisher(app.Publisher);
        Add(publishers, publisher);

        var name = CleanProductName(app.DisplayName ?? app.KeyName);
        Add(products, name);
        // "Mozilla Firefox" by "Mozilla" → "Firefox".
        if (publisher.Length > 0 && name.StartsWith(publisher + " ", StringComparison.OrdinalIgnoreCase))
            Add(products, name[(publisher.Length + 1)..]);
        // "VLC media player" → "VLC" (weaker).
        var firstWord = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstWord is not null && firstWord.Length < name.Length)
            Add(aliases, firstWord);

        foreach (var root in installRoots)
        {
            // C:\Program Files\Vendor\App → product "App", publisher "Vendor".
            Add(products, WinPath.GetFileName(root));
            if (WinPath.GetDirectoryName(root) is { } parent && !isKnownFolder(parent) && !WinPath.IsRoot(parent))
                Add(publishers, WinPath.GetFileName(parent));
        }

        // Product beats publisher beats alias: "Acme" in "Acme Editor" by "Acme" is the publisher, not an alias.
        var productSet = products.Distinct().ToList();
        var publisherSet = publishers.Distinct().Except(productSet).ToList();
        return new AppKeywords(productSet, aliases.Distinct().Except(productSet).Except(publisherSet).ToList(), publisherSet);
    }

    /// <summary>How a folder or key name relates to the application.</summary>
    public KeywordMatch Match(string name)
    {
        var n = Normalize(name);
        if (n.Length < 3)
            return KeywordMatch.None;
        if (Products.Contains(n))
            return KeywordMatch.Product;
        if (Publishers.Contains(n))
            return KeywordMatch.Publisher;
        if (Aliases.Contains(n))
            return KeywordMatch.ProductAlias;
        // "Firefox Developer Edition" / "firefox-cache": long enough to avoid accidental hits.
        if (Products.Any(p => p.Length >= 5 && (n.Contains(p) || (n.Length >= 5 && p.Contains(n)))))
            return KeywordMatch.Partial;
        return KeywordMatch.None;
    }

    /// <summary>Lower case letters, digits and '+' only ("Notepad++" → "notepad++", "7-Zip" → "7zip").</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '+')
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"7-Zip 23.01 (x64)" → "7-Zip"; "Python 3.12.1 (64-bit)" → "Python".</summary>
    public static string CleanProductName(string name)
    {
        var s = Brackets().Replace(name, " ");
        s = Architecture().Replace(s, " ");
        s = Version().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim(' ', '-', '–', ',', '.');
        return s;
    }

    /// <summary>"Mozilla Corporation" → "Mozilla"; "Igor Pavlov" stays.</summary>
    public static string CleanPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
            return "";
        var words = publisher.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && CompanySuffixes.Contains(Normalize(words[^1])))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words).Trim('.', ' ');
    }

    private static void Add(List<string> target, string text)
    {
        var n = Normalize(text);
        if (n.Length >= 3 && !StopWords.Contains(n) && !n.All(char.IsDigit))
            target.Add(n);
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\b(x64|x86|x86_64|amd64|arm64|win64|win32|64-bit|32-bit|64\s?bit|32\s?bit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Architecture();

    [GeneratedRegex(@"\b(version\s+)?v?\d+(\.\d+)+[\w.-]*|\bversion\b|\s\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex Version();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
