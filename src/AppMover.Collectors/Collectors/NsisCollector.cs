using System.Text.RegularExpressions;
using AppMover.Core.Models;
using AppMover.Core.Nsis;
using AppMover.Core.Text;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// What an NSIS uninstaller removes, read from its decompiled script: files and folders outside the install directory
/// (Start menu, data…), registry keys and values.
/// </summary>
public sealed partial class NsisCollector : ICollector
{
    public string Name => "NSIS script";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        if (context.App.Installer is not { Type: InstallerType.Nsis, UninstallerPath: { } uninstaller }
            || context.NsisScripts is not { } reader || !context.Files.FileExists(uninstaller)
            || reader.Read(uninstaller) is not { } script)
            yield break;

        var exeDir = WinPath.GetDirectoryName(uninstaller)!;
        foreach (var action in NsisScript.Parse(script))
        {
            var item = action.Kind switch
            {
                NsisActionKind.DeleteFile or NsisActionKind.RemoveDirectory => FileSystemItem(context, action, exeDir),
                _ => RegistryItem(context, action),
            };
            if (item is not null)
                yield return item;
        }
    }

    /// <summary>
    /// The script is read in order, but the uninstaller runs it through functions and branches: the shell context
    /// tracked at a line may not be the one of the run. Both contexts are tried, the existing path wins.
    /// </summary>
    private CollectedItem? FileSystemItem(CollectionContext context, NsisAction action, string exeDir)
    {
        string? path = null;
        ItemKind? kind = null;
        foreach (var allUsers in new[] { action.AllUsers, !action.AllUsers })
        {
            if (Expand(action.Target, context, allUsers, exeDir) is not { } candidate)
                continue;
            // "Delete $SMPROGRAMS\App\*.*" → the folder.
            if (candidate.IndexOfAny(['*', '?']) >= 0)
                candidate = WinPath.GetDirectoryName(candidate) ?? candidate;
            candidate = WinPath.Normalize(candidate);
            kind = context.Files.FileExists(candidate) ? ItemKind.File : context.Files.DirectoryExists(candidate) ? ItemKind.Directory : null;
            if (kind is not null)
            {
                path = candidate;
                break;
            }
        }
        if (path is null || kind is null || WinPath.IsRoot(path) || context.IsInInstallRoot(path) || context.ContainsInstallRoot(path))
            return null;

        var machine = context.Session.Machine;
        var system = WinPath.IsUnderOrEqual(path, machine.Windows);
        var shortcut = new[] { machine.CommonStartMenu, machine.CommonDesktop }
            .Concat(context.Session.Users.SelectMany(u => new[] { u.Folders.StartMenu, u.Folders.Desktop }))
            .Any(folder => WinPath.IsUnderOrEqual(path, folder));
        var confidence = system ? Confidence.Possible
            : action.Kind == NsisActionKind.RemoveDirectory && !action.Recursive ? Confidence.Probable
            : Confidence.Certain;
        return Item(Name, kind.Value, shortcut ? ItemRole.Shortcut : ItemRole.Application, confidence,
            action.Kind == NsisActionKind.DeleteFile ? "Deleted by the NSIS uninstaller" : "Folder removed by the NSIS uninstaller",
            path: path, warning: system ? "System file: it may be shared with other applications." : null);
    }

    /// <summary>
    /// SHCTX is HKLM or HKCU depending on the shell context, HKCR the machine or user classes (an elevated uninstaller
    /// writes the machine ones): both are tried, the existing key wins. Keys that do not exist are not collected.
    /// </summary>
    private CollectedItem? RegistryItem(CollectionContext context, NsisAction action)
    {
        var key = Unescape(action.Target);
        if (key.Contains('$'))
            return null;
        (int Root, bool PerMachine)[] candidates = action.Root switch
        {
            "HKCR" => [(0, true), (0, false)],
            "HKCU" => [(1, false)],
            "HKLM" => [(2, true)],
            "SHCTX" => action.AllUsers ? [(2, true), (1, false)] : [(1, false), (2, true)],
            _ => [],
        };
        RegistryLocation? location = null;
        foreach (var (root, perMachine) in candidates)
        {
            if (context.InstallerRegistryLocation(root, key, action.RegView64, perMachine) is not { } candidate)
                continue;
            using var existing = context.Open(candidate);
            if (existing is null)
                continue;
            location = candidate;
            break;
        }
        if (location is null)
            return null;

        return action.Kind == NsisActionKind.DeleteRegistryKey
            ? Item(Name, ItemKind.RegistryKey, ItemRole.Settings, action.IfEmpty ? Confidence.Possible : Confidence.Certain,
                action.IfEmpty ? "Registry key removed by the NSIS uninstaller when empty (may be shared)" : "Registry key removed by the NSIS uninstaller",
                registry: location)
            : Item(Name, ItemKind.RegistryValue, ItemRole.Settings, Confidence.Certain, "Registry value removed by the NSIS uninstaller",
                registry: location, valueName: Unescape(action.ValueName ?? ""));
    }

    /// <summary>
    /// Expands NSIS variables as seen by the (32-bit) uninstaller: $PROGRAMFILES is Program Files (x86), $SYSDIR is SysWOW64,
    /// shell folders depend on SetShellVarContext. Null when a variable is unknown ($TEMP, $0…).
    /// </summary>
    private static string? Expand(string text, CollectionContext context, bool allUsers, string exeDir)
    {
        var machine = context.Session.Machine;
        var user = context.Owner;
        var unknown = false;

        string? Folder(string name) => name.ToUpperInvariant() switch
        {
            "INSTDIR" or "EXEDIR" or "OUTDIR" => exeDir,
            "PROGRAMFILES" or "PROGRAMFILES32" or "PROGRAM_FILES" or "PROGRAM_FILESX86" => machine.ProgramFilesX86,
            "PROGRAMFILES64" => machine.ProgramFiles,
            "COMMONFILES" or "COMMONFILES32" or "PROGRAM_FILES_COMMON" or "PROGRAM_FILES_COMMONX8" or "PROGRAM_FILES_COMMONX86"
                => WinPath.Combine(machine.ProgramFilesX86, "Common Files"),
            "COMMONFILES64" => WinPath.Combine(machine.ProgramFiles, "Common Files"),
            "WINDIR" => machine.Windows,
            "SYSDIR" or "SYSTEMX86" => machine.SysWow64,
            "APPDATA" or "LOCALAPPDATA" when allUsers => machine.ProgramData,
            "APPDATA" => user?.Folders.AppData,
            "LOCALAPPDATA" => user?.Folders.LocalAppData,
            "SMPROGRAMS" => WinPath.Combine(allUsers ? machine.CommonStartMenu : user?.Folders.StartMenu ?? "", "Programs"),
            "STARTMENU" => allUsers ? machine.CommonStartMenu : user?.Folders.StartMenu,
            "SMSTARTUP" => WinPath.Combine(allUsers ? machine.CommonStartMenu : user?.Folders.StartMenu ?? "", @"Programs\Startup"),
            "DESKTOP" => allUsers ? machine.CommonDesktop : user?.Folders.Desktop,
            "DOCUMENTS" => allUsers ? WinPath.Combine(machine.Public, "Documents") : user?.Folders.Documents,
            "PROFILE" => user?.Folders.Profile,
            _ => null,
        };

        var expanded = Variable().Replace(text, m =>
        {
            var token = m.Groups[1].Value;
            switch (token)
            {
                case "$": return "$";
                case @"\""": return "\"";
                case @"\t": return "\t";
                case @"\n": return "\n";
                case @"\r": return "\r";
            }
            var folder = Folder(token.Trim('{', '}'));
            // Unknown variable, or user folder without user ("Programs" instead of "C:\…\Programs").
            if (folder is null || folder.Length < 3 || folder[1] != ':')
                unknown = true;
            return folder ?? m.Value;
        });
        return unknown ? null : expanded;
    }

    private static string Unescape(string text) => text.Replace("$$", "$").Replace("$\\\"", "\"");

    [GeneratedRegex(@"\$(\$|\\[""tnr]|\{[A-Za-z_][A-Za-z0-9_]*\}|[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Variable();
}
