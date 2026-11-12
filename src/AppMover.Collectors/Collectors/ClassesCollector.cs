using AppMover.Core.Models;
using AppMover.Core.Registry;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// HKEY_CLASSES_ROOT registrations (machine and per-user): COM classes and type libraries,
/// ProgIDs / URL protocols whose commands run the application, and the file extensions using them.
/// </summary>
public sealed class ClassesCollector : ICollector
{
    private static readonly string[] ComServerKeys = ["InprocServer32", "LocalServer32", "InprocHandler32"];
    private static readonly HashSet<string> NonProgIdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "TypeLib", "Interface", "AppID", "WOW6432Node", "Applications", "Installer", "SystemFileAssociations",
        "MIME", "Local Settings", "VirtualStore",
    };

    public string Name => "Classes";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        if (context.InstallRoots.Count == 0)
            yield break;

        // Machine classes: COM keys are redirected for 32-bit, the others are shared (64-bit view only).
        var machine64 = RegistryLocation.Machine(RegView.Registry64, @"SOFTWARE\Classes");
        var machine32 = RegistryLocation.Machine(RegView.Registry32, @"SOFTWARE\Classes");
        var scopes = new List<(RegistryScope Scope, bool ComOnly)>
        {
            (new RegistryScope(machine64, null), false),
            (new RegistryScope(machine32, null), true),
        };
        scopes.AddRange(context.Session.Users.Select(u => (new RegistryScope(RegistryLocation.User(u.Sid, @"Software\Classes"), u), false)));

        foreach (var (scope, comOnly) in scopes)
        {
            using var classes = context.Open(scope.Location);
            if (classes is null)
                continue;

            var progIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in CollectCom(context, scope, classes, progIds))
                yield return item;
            foreach (var item in CollectTypeLibs(context, scope, classes))
                yield return item;
            if (comOnly)
                continue;

            foreach (var item in CollectProgIds(context, scope, classes, progIds))
                yield return item;
            foreach (var item in CollectExtensions(scope, classes, progIds))
                yield return item;
        }
    }

    private IEnumerable<CollectedItem> CollectCom(CollectionContext context, RegistryScope scope, IRegistryKey classes, HashSet<string> progIds)
    {
        using var clsids = classes.OpenSubKey("CLSID");
        if (clsids is null)
            yield break;

        foreach (var clsid in clsids.GetSubKeyNames())
        {
            using var key = clsids.OpenSubKey(clsid);
            if (key is null)
                continue;

            string? root = null;
            foreach (var server in ComServerKeys)
            {
                using var serverKey = key.OpenSubKey(server);
                root ??= context.FindReferencedRoot(serverKey?.GetString(""), scope.User);
            }
            if (root is null)
                continue;

            yield return Item(Name, ItemKind.RegistryKey, ItemRole.Com, Confidence.Certain, $"COM class served from {root}",
                registry: Sub(scope, $@"CLSID\{clsid}"));

            foreach (var progIdKey in new[] { "ProgID", "VersionIndependentProgID" })
            {
                using var sub = key.OpenSubKey(progIdKey);
                if (sub?.GetString("") is { Length: > 0 } progId && progIds.Add(progId))
                    yield return Item(Name, ItemKind.RegistryKey, ItemRole.Com, Confidence.Certain, $"ProgID of COM class {clsid}",
                        registry: Shared(scope, progId));
            }
            if (key.GetString("AppID") is { Length: > 0 } appId && !appId.Equals(clsid, StringComparison.OrdinalIgnoreCase))
                yield return Item(Name, ItemKind.RegistryKey, ItemRole.Com, Confidence.Probable, $"AppID of COM class {clsid}",
                    registry: Sub(scope, $@"AppID\{appId}"));
        }
    }

    private IEnumerable<CollectedItem> CollectTypeLibs(CollectionContext context, RegistryScope scope, IRegistryKey classes)
    {
        using var typeLibs = classes.OpenSubKey("TypeLib");
        if (typeLibs is null)
            yield break;

        foreach (var libId in typeLibs.GetSubKeyNames())
        {
            using var lib = typeLibs.OpenSubKey(libId);
            if (lib is not null && FindTypeLibRoot(context, scope, lib) is { } root)
                yield return Item(Name, ItemKind.RegistryKey, ItemRole.Com, Confidence.Certain, $"Type library in {root}",
                    registry: Sub(scope, $@"TypeLib\{libId}"));
        }
    }

    /// <summary>TypeLib\{id}\{version}\{lcid}\win32|win64 → path.</summary>
    private static string? FindTypeLibRoot(CollectionContext context, RegistryScope scope, IRegistryKey lib)
    {
        foreach (var version in lib.GetSubKeyNames())
        {
            using var versionKey = lib.OpenSubKey(version);
            foreach (var lcid in versionKey?.GetSubKeyNames() ?? [])
            {
                using var lcidKey = versionKey!.OpenSubKey(lcid);
                foreach (var platform in lcidKey?.GetSubKeyNames() ?? [])
                {
                    using var platformKey = lcidKey!.OpenSubKey(platform);
                    if (context.FindReferencedRoot(platformKey?.GetString(""), scope.User) is { } root)
                        return root;
                }
            }
        }
        return null;
    }

    private IEnumerable<CollectedItem> CollectProgIds(CollectionContext context, RegistryScope scope, IRegistryKey classes, HashSet<string> progIds)
    {
        foreach (var name in classes.GetSubKeyNames())
        {
            if (name.StartsWith('.') || name.StartsWith('*') || NonProgIdKeys.Contains(name) || progIds.Contains(name))
                continue;
            using var key = classes.OpenSubKey(name);
            if (key is null || FindCommandRoot(context, scope, key) is not { } root)
                continue;

            progIds.Add(name);
            var isProtocol = key.GetValue("URL Protocol") is not null;
            yield return Item(Name, ItemKind.RegistryKey, ItemRole.FileAssociation, Confidence.Certain,
                $"{(isProtocol ? "URL protocol" : "File type")} '{name}' opened with {root}",
                registry: Shared(scope, name));
        }

        // Classes\Applications\app.exe (Open with…).
        using var applications = classes.OpenSubKey("Applications");
        foreach (var exe in applications?.GetSubKeyNames() ?? [])
        {
            using var key = applications!.OpenSubKey(exe);
            if (key is not null && FindCommandRoot(context, scope, key) is { } root)
                yield return Item(Name, ItemKind.RegistryKey, ItemRole.FileAssociation, Confidence.Certain,
                    $"'Open with' registration of {exe} in {root}", registry: Shared(scope, $@"Applications\{exe}"));
        }
    }

    /// <summary>shell\&lt;verb&gt;\command of a class.</summary>
    private static string? FindCommandRoot(CollectionContext context, RegistryScope scope, IRegistryKey classKey)
    {
        using var shell = classKey.OpenSubKey("shell");
        if (shell is null)
            return null;
        foreach (var verb in shell.GetSubKeyNames())
        {
            using var command = shell.OpenSubKey($@"{verb}\command");
            if (context.FindReferencedRoot(command?.GetString(""), scope.User) is { } root)
                return root;
        }
        return null;
    }

    private IEnumerable<CollectedItem> CollectExtensions(RegistryScope scope, IRegistryKey classes, HashSet<string> progIds)
    {
        if (progIds.Count == 0)
            yield break;

        foreach (var extension in classes.GetSubKeyNames().Where(n => n.StartsWith('.')))
        {
            using var key = classes.OpenSubKey(extension);
            if (key is null)
                continue;

            var location = Shared(scope, extension);
            if (key.GetString("") is { } defaultProgId && progIds.Contains(defaultProgId))
                yield return Item(Name, ItemKind.RegistryValue, ItemRole.FileAssociation, Confidence.Probable,
                    $"{extension} files open with {defaultProgId} by default", registry: location, valueName: "",
                    warning: "Changes the default program of this extension on the target machine.");

            using var openWith = key.OpenSubKey("OpenWithProgids");
            foreach (var progId in openWith?.GetValueNames() ?? [])
            {
                if (progIds.Contains(progId))
                    yield return Item(Name, ItemKind.RegistryValue, ItemRole.FileAssociation, Confidence.Certain,
                        $"{progId} proposed to open {extension} files",
                        registry: location with { Path = $@"{location.Path}\OpenWithProgids" }, valueName: progId);
            }
        }
    }

    /// <summary>Key under the classes root of this scope (same view).</summary>
    private static RegistryLocation Sub(RegistryScope scope, string path) =>
        scope.Location with { Path = $@"{scope.Location.Path}\{path}" };

    /// <summary>Non-COM classes are shared between views: always use the 64-bit view.</summary>
    private static RegistryLocation Shared(RegistryScope scope, string path) =>
        (scope.Location.Hive == RegHive.LocalMachine ? scope.Location with { View = RegView.Registry64 } : scope.Location)
        with { Path = $@"{scope.Location.Path}\{path}" };
}
