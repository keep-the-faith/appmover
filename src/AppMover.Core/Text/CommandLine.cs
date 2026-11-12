namespace AppMover.Core.Text;

public static class CommandLine
{
    /// <summary>
    /// Extracts the executable from a command line such as an UninstallString.
    /// Handles quoted paths and unquoted paths containing spaces
    /// (<c>C:\Program Files\App\uninst.exe /S</c>). Environment variables are not expanded.
    /// </summary>
    public static string? ExtractExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var s = commandLine.Trim();
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            var quoted = end < 0 ? s[1..] : s[1..end];
            return quoted.Length == 0 ? null : quoted;
        }

        var exeEnd = IndexOfExecutableEnd(s);
        if (exeEnd > 0)
            return s[..exeEnd];

        var space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    private static int IndexOfExecutableEnd(string s)
    {
        var index = 0;
        while ((index = s.IndexOf(".exe", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = index + 4;
            if (end == s.Length || s[end] is ' ' or '/' or '"' or ',' or '\t')
                return end;
            index = end;
        }
        return -1;
    }
}
