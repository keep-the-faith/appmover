using AppMover.Core.Models;
using AppMover.Core.Text;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// Files and folders listed in installer logs whose format is not documented — InstallShield (setup.ilg), Wise and
/// custom installers (install.log…) — found by searching the logs for absolute paths that exist on the disk.
/// </summary>
public sealed class InstallerLogCollector : ICollector
{
    private const long MaxLogSize = 16 * 1024 * 1024;
    private static readonly string[] LogNames = ["install.log", "uninstall.log", "setup.log", "INSTALL.LOG", "uninst.log"];

    public string Name => "Installer log";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        var windows = context.Session.Machine.Windows;
        var shellFolders = ShellFolders(context);
        var seen = new HashSet<string>(WinPath.Comparer);

        foreach (var log in FindLogs(context))
        {
            byte[] data;
            try
            {
                using var stream = context.Files.OpenRead(log);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                data = memory.ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in PathStrings.Extract(data).Select(WinPath.Normalize))
            {
                if (!seen.Add(path) || WinPath.IsRoot(path) || context.IsInInstallRoot(path) || context.ContainsInstallRoot(path)
                    || WinPath.IsUnderOrEqual(log, path) || shellFolders.Any(f => WinPath.IsUnderOrEqual(path, f)))
                    continue;
                var kind = context.Files.FileExists(path) ? ItemKind.File : context.Files.DirectoryExists(path) ? ItemKind.Directory : (ItemKind?)null;
                if (kind is null)
                    continue;

                // A folder named in a log is mostly a parent (Program Files, Temp, OneDrive…): only files and folders
                // named after the application count.
                if (kind == ItemKind.Directory && (IsKnownOrParentOfKnown(context, path) || !IsNamedAfterApp(context, path)))
                    continue;
                var system = WinPath.IsUnderOrEqual(path, windows);
                yield return Item(Name, kind.Value, ItemRole.Application, system ? Confidence.Possible : Confidence.Probable,
                    $"Listed in the installer log {WinPath.GetFileName(log)}", path: path,
                    warning: system ? "System file: it may be shared with other applications." : null);
            }
        }
    }

    /// <summary>InstallShield logs next to its uninstaller, the LogFile of the Uninstall entry, usual log names in the install folders.</summary>
    private static IEnumerable<string> FindLogs(CollectionContext context)
    {
        var logs = new List<string>();
        if (context.App.RawValues.TryGetValue("LogFile", out var logFile) && logFile.AsString() is { Length: > 0 } declared)
            logs.Add(context.Expand(declared, context.Owner));
        if (context.App.Installer?.UninstallerPath is { } uninstaller
            && uninstaller.Contains(@"\InstallShield Installation Information\", StringComparison.OrdinalIgnoreCase)
            && WinPath.GetDirectoryName(uninstaller) is { } setupDir)
            logs.AddRange(context.Files.EnumerateFiles(setupDir, "*.ilg"));
        foreach (var root in context.InstallRoots)
            logs.AddRange(LogNames.Select(name => WinPath.Combine(root.Path, name)));

        return logs
            .Select(WinPath.Normalize)
            .Distinct(WinPath.Comparer)
            .Where(l => context.Files.GetFileMeta(l) is { Length: > 0 and <= MaxLogSize });
    }

    /// <summary>
    /// Start menus and desktops: logs name the folders the setup touched there (Programs\Accessibility…), which are
    /// shared with Windows. The application's shortcuts are found by the shortcut collector, which checks their target.
    /// </summary>
    private static List<string> ShellFolders(CollectionContext context) =>
        new[] { context.Session.Machine.CommonStartMenu, context.Session.Machine.CommonDesktop }
            .Concat(context.Session.Users.SelectMany(u => new[] { u.Folders.StartMenu, u.Folders.Desktop }))
            .ToList();

    /// <summary>The folder or one of its parents is named after the application or its publisher (Realtek\Audio\InstallShield).</summary>
    private static bool IsNamedAfterApp(CollectionContext context, string path) =>
        path.Split('\\').Skip(1).Any(segment =>
            context.Keywords.Match(segment) is KeywordMatch.Product or KeywordMatch.ProductAlias or KeywordMatch.Publisher);

    private static bool IsKnownOrParentOfKnown(CollectionContext context, string path) =>
        context.Session.Machine.All().Select(f => f.Path)
            .Concat(context.Session.Users.SelectMany(u => u.Folders.All().Select(f => f.Path)))
            .Concat([WinPath.Combine(context.Session.Machine.ProgramFiles, "Common Files"), WinPath.Combine(context.Session.Machine.ProgramFilesX86, "Common Files")])
            .Any(known => WinPath.IsUnderOrEqual(known, path));
}
