namespace AppMover.Core.Text;

/// <summary>Windows path helpers that behave the same on every OS (unit tests run on Linux too).</summary>
public static class WinPath
{
    private static readonly char[] Separators = ['\\', '/'];

    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public static string GetFileName(string path)
    {
        var index = path.LastIndexOfAny(Separators);
        return index < 0 ? path : path[(index + 1)..];
    }

    public static string? GetDirectoryName(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var index = trimmed.LastIndexOfAny(Separators);
        if (index < 0)
            return null;
        // Keep the root separator: "C:\file" → "C:\".
        return index > 0 && trimmed[index - 1] == ':' ? trimmed[..(index + 1)] : trimmed[..index];
    }

    public static string ChangeExtension(string path, string extension)
    {
        var name = GetFileName(path);
        var dot = name.LastIndexOf('.');
        var basePath = dot < 0 ? path : path[..(path.Length - name.Length + dot)];
        return basePath + extension;
    }

    public static string Combine(string directory, string name) =>
        directory.Length == 0 ? name : directory.TrimEnd(Separators) + "\\" + name.TrimStart(Separators);

    public static bool FileNameEquals(string path, string fileName) =>
        string.Equals(GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Backslashes, no trailing separator (except for a drive root "C:\"), no surrounding quotes.</summary>
    public static string Normalize(string path)
    {
        var p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            p = p[4..];
        p = p.TrimEnd('\\');
        return p.Length == 2 && p[1] == ':' ? p + "\\" : p;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or is inside it.</summary>
    public static bool IsUnderOrEqual(string path, string root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = r.EndsWith('\\') ? r : r + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for "C:\" or "\\server\share".</summary>
    public static bool IsRoot(string path)
    {
        var p = Normalize(path);
        if (p.Length <= 3 && p.Length >= 2 && p[1] == ':')
            return true;
        return p.StartsWith(@"\\", StringComparison.Ordinal) && p[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
    }
}
