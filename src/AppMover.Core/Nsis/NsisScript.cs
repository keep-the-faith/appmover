namespace AppMover.Core.Nsis;

/// <summary>Extracts the decompiled script of an NSIS executable (7-Zip's "[NSIS].nsi").</summary>
public interface INsisScriptReader
{
    /// <summary>The script, or null when it cannot be extracted (tool missing, not an NSIS file…).</summary>
    string? Read(string executablePath);
}

public enum NsisActionKind { DeleteFile, RemoveDirectory, DeleteRegistryKey, DeleteRegistryValue }

/// <summary>
/// A removal performed by an NSIS uninstaller. Paths and keys are raw (NSIS variables not expanded);
/// <see cref="AllUsers"/> and <see cref="RegView64"/> give the state of SetShellVarContext / SetRegView at that point.
/// </summary>
public sealed record NsisAction(NsisActionKind Kind, string Target)
{
    /// <summary>Registry root as printed by 7-Zip: SHCTX, HKCR, HKCU, HKLM, HKU…</summary>
    public string? Root { get; init; }
    public string? ValueName { get; init; }
    /// <summary>RMDir /r.</summary>
    public bool Recursive { get; init; }
    /// <summary>DeleteRegKey /ifempty.</summary>
    public bool IfEmpty { get; init; }
    public bool AllUsers { get; init; }
    public bool RegView64 { get; init; }
}

/// <summary>
/// Reads the removal commands of a script decompiled by 7-Zip (NsisIn.cpp): one command per line, arguments separated
/// by spaces, double-quoted when they contain a space; "$$" = "$", "$\"" = '"'.
/// </summary>
public static class NsisScript
{
    public static IReadOnlyList<NsisAction> Parse(string script)
    {
        var actions = new List<NsisAction>();
        var allUsers = false;
        var view64 = false;

        foreach (var line in script.Split('\n'))
        {
            var tokens = Tokenize(line);
            if (tokens.Count == 0)
                continue;
            var arguments = tokens.Skip(1).Where(t => !t.StartsWith('/')).ToList();
            var flags = tokens.Skip(1).Where(t => t.StartsWith('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            NsisAction? action = null;

            switch (tokens[0])
            {
                case "SetShellVarContext" when arguments.Count > 0:
                    allUsers = arguments[0].Equals("all", StringComparison.OrdinalIgnoreCase);
                    break;
                case "SetRegView" when arguments.Count > 0:
                    view64 = arguments[0] == "64";
                    break;
                case "Delete" when arguments.Count > 0:
                    action = new NsisAction(NsisActionKind.DeleteFile, arguments[0]);
                    break;
                case "RMDir" when arguments.Count > 0:
                    action = new NsisAction(NsisActionKind.RemoveDirectory, arguments[0]) { Recursive = flags.Contains("/r") };
                    break;
                case "DeleteRegKey" when arguments.Count > 1:
                    action = new NsisAction(NsisActionKind.DeleteRegistryKey, arguments[1]) { Root = arguments[0], IfEmpty = flags.Contains("/ifempty") };
                    break;
                case "DeleteRegValue" when arguments.Count > 2:
                    action = new NsisAction(NsisActionKind.DeleteRegistryValue, arguments[1]) { Root = arguments[0], ValueName = arguments[2] };
                    break;
            }
            if (action is not null)
                actions.Add(action with { AllUsers = allUsers, RegView64 = view64 });
        }
        return actions;
    }

    /// <summary>Splits a line in arguments; a ';' or '#' outside quotes starts a comment.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        void Flush()
        {
            if (hasToken)
                tokens.Add(current.ToString());
            current.Clear();
            hasToken = false;
        }

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '$' && i + 1 < line.Length && line[i + 1] == '\\' && i + 2 < line.Length && line[i + 2] == '"')
            {
                current.Append("$\\\"");
                hasToken = true;
                i += 2;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                Flush();
            }
            else if (!inQuotes && !hasToken && c is ';' or '#')
            {
                break;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        Flush();
        return tokens;
    }
}
