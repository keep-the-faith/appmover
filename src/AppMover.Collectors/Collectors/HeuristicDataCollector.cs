using AppMover.Core.Models;
using AppMover.Core.Text;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// Data and settings that the uninstaller does not know about, found by name in the usual locations
/// (AppData, ProgramData, Documents, HKLM/HKCU\Software, UAC VirtualStore).
/// </summary>
public sealed class HeuristicDataCollector : ICollector
{
    public string Name => "Heuristic";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        var machine = context.Session.Machine;
        var folders = new List<(string Path, SessionUser? User, Confidence Max)> { (machine.ProgramData, null, Confidence.Probable) };
        foreach (var user in context.Session.Users)
        {
            folders.Add((user.Folders.AppData, user, Confidence.Probable));
            folders.Add((user.Folders.LocalAppData, user, Confidence.Probable));
            folders.Add((user.Folders.LocalAppDataLow, user, Confidence.Probable));
            // Documents mostly hold the user's own files: never selected by default.
            folders.Add((user.Folders.Documents, user, Confidence.Possible));
        }

        foreach (var (path, user, max) in folders)
        {
            foreach (var item in ScanFolder(context, path, user, max))
                yield return item;
        }

        foreach (var scope in context.SoftwareScopes(""))
        {
            foreach (var item in ScanRegistry(context, scope))
                yield return item;
        }

        foreach (var user in context.Session.Users)
        {
            foreach (var item in ScanVirtualStore(context, user))
                yield return item;
        }
    }

    private IEnumerable<CollectedItem> ScanFolder(CollectionContext context, string location, SessionUser? user, Confidence max)
    {
        var role = location.Equals(user?.Folders.Documents, StringComparison.OrdinalIgnoreCase) ? ItemRole.Data : ItemRole.Settings;
        foreach (var (path, confidence, reason) in Scan(context, context.Files.EnumerateDirectories(location), context.Files.EnumerateDirectories))
        {
            if (context.IsInInstallRoot(path) || context.ContainsInstallRoot(path))
                continue;
            yield return Item(Name, ItemKind.Directory, role, Min(confidence, max), reason, path: path, user: user);
        }
    }

    private IEnumerable<CollectedItem> ScanRegistry(CollectionContext context, RegistryScope scope)
    {
        using var software = context.Open(scope.Location);
        if (software is null)
            yield break;

        IEnumerable<string> Children(string path)
        {
            using var key = context.Open(scope.Location with { Path = path });
            return key?.GetSubKeyNames().Select(n => $@"{path}\{n}").ToList() ?? [];
        }

        var top = software.GetSubKeyNames().Select(n => $@"{scope.Location.Path}\{n}");
        foreach (var (path, confidence, reason) in Scan(context, top, Children))
            yield return Item(Name, ItemKind.RegistryKey, ItemRole.Settings, confidence, reason, registry: scope.Location with { Path = path });
    }

    /// <summary>Files and keys written by legacy applications without admin rights are redirected per user.</summary>
    private IEnumerable<CollectedItem> ScanVirtualStore(CollectionContext context, SessionUser user)
    {
        var store = WinPath.Combine(user.Folders.LocalAppData, "VirtualStore");
        foreach (var root in context.InstallRoots)
        {
            // C:\Program Files (x86)\App → %LOCALAPPDATA%\VirtualStore\Program Files (x86)\App
            var virtualPath = WinPath.Combine(store, root.Path.Length > 3 ? root.Path[3..] : root.Path);
            if (context.Files.DirectoryExists(virtualPath))
                yield return Item(Name, ItemKind.Directory, ItemRole.Settings, Confidence.Certain,
                    "Files written by the application in its install folder, redirected by UAC", path: virtualPath, user: user);
        }

        foreach (var softwarePath in new[] { @"Software\Classes\VirtualStore\MACHINE\SOFTWARE", @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node" })
        {
            foreach (var item in ScanRegistry(context, new RegistryScope(RegistryLocation.User(user.Sid, softwarePath), user)))
                yield return item with { Reason = item.Reason + " (UAC VirtualStore)" };
        }
    }

    /// <summary>
    /// Two-level search: "&lt;Product&gt;" and "&lt;Publisher&gt;\&lt;Product&gt;" are probable,
    /// partial names and publisher folders without a product sub-folder are possible.
    /// </summary>
    private static IEnumerable<(string Path, Confidence Confidence, string Reason)> Scan(
        CollectionContext context, IEnumerable<string> topLevel, Func<string, IEnumerable<string>> children)
    {
        var keywords = context.Keywords;
        foreach (var path in topLevel)
        {
            var name = WinPath.GetFileName(path);
            switch (keywords.Match(name))
            {
                case KeywordMatch.Product:
                    yield return (path, Confidence.Probable, $"Named after the application ('{name}')");
                    break;
                case KeywordMatch.ProductAlias:
                case KeywordMatch.Partial:
                    yield return (path, Confidence.Possible, $"Name close to the application ('{name}')");
                    break;
                case KeywordMatch.Publisher:
                    var found = false;
                    foreach (var child in children(path))
                    {
                        var childName = WinPath.GetFileName(child);
                        switch (keywords.Match(childName))
                        {
                            case KeywordMatch.Product:
                                found = true;
                                yield return (child, Confidence.Probable, $"Publisher\\application folder ('{name}\\{childName}')");
                                break;
                            case KeywordMatch.ProductAlias:
                            case KeywordMatch.Partial:
                                found = true;
                                yield return (child, Confidence.Possible, $"Publisher folder, name close to the application ('{name}\\{childName}')");
                                break;
                        }
                    }
                    if (!found && keywords.IsSpecificPublisher(AppKeywords.Normalize(name)))
                        yield return (path, Confidence.Possible, $"Publisher folder ('{name}')");
                    break;
            }
        }
    }

    private static Confidence Min(Confidence a, Confidence b) => (Confidence)Math.Min((int)a, (int)b);
}
