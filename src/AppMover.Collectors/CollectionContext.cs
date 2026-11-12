using AppMover.Core.Models;
using AppMover.Core.Msi;
using AppMover.Core.Nsis;
using AppMover.Core.Tracing;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Text;

namespace AppMover.Collectors;

/// <summary>A registry location to scan, with the user it belongs to (null for HKLM).</summary>
public sealed record RegistryScope(RegistryLocation Location, SessionUser? User);

/// <summary>Everything a collector needs about the application and the machine.</summary>
public sealed class CollectionContext(
    InstalledApp app, IRegistrySource registry, IFileSystem files, IPathResolver paths, CollectionSession session)
{
    private IReadOnlyList<InstallRoot> _installRoots = [];

    public InstalledApp App => app;
    public IRegistrySource Registry => registry;
    public IFileSystem Files => files;
    public IPathResolver Paths => paths;
    public CollectionSession Session => session;

    /// <summary>Install directories of the application (set before the collectors run).</summary>
    public IReadOnlyList<InstallRoot> InstallRoots
    {
        get => _installRoots;
        internal set => _installRoots = value;
    }

    /// <summary>The main install directory (becomes {InstallDir} in the package).</summary>
    public string? PrimaryInstallRoot => _installRoots.FirstOrDefault(r => r.IsPrimary)?.Path;

    public AppKeywords Keywords { get; internal set; } = AppKeywords.Empty;

    /// <summary>
    /// Windows Installer registration and components: the product of an MSI application, or the packages chained by a
    /// WiX Burn bundle (largest first).
    /// </summary>
    public IReadOnlyList<MsiProduct> MsiProducts { get; internal set; } = [];

    /// <summary>Reads the cached .msi database (null when not available, e.g. in tests or on non-Windows).</summary>
    public IMsiDatabaseReader? MsiDatabase { get; internal set; }

    /// <summary>Extracts the script of NSIS uninstallers (null when not available).</summary>
    public INsisScriptReader? NsisScripts { get; internal set; }

    /// <summary>What the application used while it was traced (dynamic analysis), if a trace was recorded.</summary>
    public UsageTrace? UsageTrace { get; internal set; }

    /// <summary>The user who installed the application: owner of a per-user app, otherwise the first user of the session.</summary>
    public SessionUser? Owner => session.FindUser(app.UserSid) ?? session.Users.FirstOrDefault();

    /// <summary>
    /// Location of a key written by an installer. <paramref name="root"/>: 0 HKCR, 1 HKCU, 2 HKLM, -1 HKLM or HKCU
    /// depending on <paramref name="perMachine"/>. HKCR is HKLM\SOFTWARE\Classes for per-machine installs, HKCU's otherwise.
    /// </summary>
    public RegistryLocation? InstallerRegistryLocation(int root, string key, bool is64Bit, bool perMachine)
    {
        var view = is64Bit ? RegView.Registry64 : RegView.Registry32;
        RegistryLocation? User(string path) => Owner is { } owner ? RegistryLocation.User(owner.Sid, path) : null;
        return root switch
        {
            0 => perMachine ? RegistryLocation.Machine(view, $@"SOFTWARE\Classes\{key}") : User($@"Software\Classes\{key}"),
            1 => User(key),
            2 => RegistryLocation.Machine(view, key),
            -1 => perMachine ? RegistryLocation.Machine(view, key) : User(key),
            _ => null,
        };
    }

    /// <summary>
    /// Items found by the <see cref="CollectionPhase.Main"/> collectors, available to the <see cref="CollectionPhase.Dependent"/> ones.
    /// </summary>
    public IReadOnlyList<CollectedItem> MainItems
    {
        get => _mainItems;
        internal set
        {
            _mainItems = value;
            _dataFolders = value
                .Where(i => i.Kind == ItemKind.Directory && i.Role is ItemRole.Settings or ItemRole.Data)
                .Select(i => new ApplicationFolder(i.Path!, i.Confidence))
                .ToList();
            _comClassIds = value
                .Where(i => i.Kind == ItemKind.RegistryKey && i.Role == ItemRole.Com)
                .Select(i => (Name: WinPath.GetFileName(i.Registry!.Path), Parent: WinPath.GetDirectoryName(i.Registry.Path), i.Confidence))
                .Where(c => c.Parent is not null && WinPath.FileNameEquals(c.Parent, "CLSID"))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(c => c.Confidence), StringComparer.OrdinalIgnoreCase);
        }
    }

    private IReadOnlyList<CollectedItem> _mainItems = [];
    private IReadOnlyList<ApplicationFolder> _dataFolders = [];
    private IReadOnlyDictionary<string, Confidence> _comClassIds = new Dictionary<string, Confidence>();

    /// <summary>Confidence of a COM class collected in the main phase, or null.</summary>
    public Confidence? FindComClass(string? clsid) =>
        clsid is not null && _comClassIds.TryGetValue(clsid.Trim(), out var confidence) ? confidence : null;

    public IRegistryKey? Open(RegistryLocation location) => session.OpenKey(registry, location);

    /// <summary>
    /// HKLM (64-bit, and 32-bit when <paramref name="bothViews"/>) and every session user for a path under SOFTWARE
    /// (<paramref name="softwarePath"/> is relative to SOFTWARE, e.g. <c>Microsoft\Windows\CurrentVersion\Run</c>).
    /// </summary>
    public IEnumerable<RegistryScope> SoftwareScopes(string softwarePath, bool bothViews = true)
    {
        var suffix = softwarePath.Length == 0 ? "" : @"\" + softwarePath;
        yield return new RegistryScope(RegistryLocation.Machine(RegView.Registry64, "SOFTWARE" + suffix), null);
        if (bothViews)
            yield return new RegistryScope(RegistryLocation.Machine(RegView.Registry32, "SOFTWARE" + suffix), null);
        foreach (var user in session.Users)
            yield return new RegistryScope(RegistryLocation.User(user.Sid, "Software" + suffix), user);
    }

    /// <summary>Expands environment variables; user variables are resolved for <paramref name="user"/> when given.</summary>
    public string Expand(string text, SessionUser? user)
    {
        if (user is not null)
        {
            text = text
                .Replace("%USERPROFILE%", user.Folders.Profile, StringComparison.OrdinalIgnoreCase)
                .Replace("%APPDATA%", user.Folders.AppData, StringComparison.OrdinalIgnoreCase)
                .Replace("%LOCALAPPDATA%", user.Folders.LocalAppData, StringComparison.OrdinalIgnoreCase);
        }
        return paths.Expand(text);
    }

    /// <summary>Returns the install root referenced by a path or command line, or null.</summary>
    public string? FindReferencedRoot(string? text, SessionUser? user = null) =>
        FindReferenced(text, user, _installRoots.Select(r => r.Path));

    /// <summary>
    /// Folder of the application referenced by a path or command line: an install root (Certain), or a data folder found
    /// in the main phase (at most Probable, since data folders are found by name).
    /// </summary>
    public ApplicationFolder? FindReferencedFolder(string? text, SessionUser? user = null)
    {
        if (FindReferencedRoot(text, user) is { } root)
            return new ApplicationFolder(root, Confidence.Certain);
        var folder = FindReferenced(text, user, _dataFolders.Select(f => f.Path));
        if (folder is null)
            return null;
        var confidence = _dataFolders.Where(f => f.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)).Max(f => f.Confidence);
        return new ApplicationFolder(folder, confidence > Confidence.Probable ? Confidence.Probable : confidence);
    }

    private string? FindReferenced(string? text, SessionUser? user, IEnumerable<string> folders)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var expanded = Expand(text, user).Replace('/', '\\');
        foreach (var folder in folders)
        {
            var index = 0;
            while ((index = expanded.IndexOf(folder, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var end = index + folder.Length;
                // "C:\App" must not match "C:\Apps".
                if (end == expanded.Length || expanded[end] is '\\' or '"' or '\'' or ' ' or ',' or ';' or '|')
                    return folder;
                index = end;
            }
        }
        return null;
    }

    /// <summary>True when the path is (inside) an install root.</summary>
    public bool IsInInstallRoot(string path) => _installRoots.Any(r => WinPath.IsUnderOrEqual(path, r.Path));

    /// <summary>True when the path contains an install root (e.g. %LOCALAPPDATA%\Programs).</summary>
    public bool ContainsInstallRoot(string path) => _installRoots.Any(r => WinPath.IsUnderOrEqual(r.Path, path));

    /// <summary>
    /// True for a folder too broad to belong to an application: a drive root, or a folder that is (or contains) a known
    /// folder (Program Files, Common Files, Windows, a user profile…).
    /// </summary>
    public bool IsTooBroad(string path)
    {
        if (WinPath.IsRoot(path))
            return true;
        var machine = session.Machine;
        return machine.All().Select(f => f.Path)
            .Concat(session.Users.SelectMany(u => u.Folders.All().Select(f => f.Path)))
            .Concat([WinPath.Combine(machine.ProgramFiles, "Common Files"), WinPath.Combine(machine.ProgramFilesX86, "Common Files")])
            .Any(k => WinPath.IsUnderOrEqual(k, path));
    }

    /// <summary>Factory for items, filling the owner of per-user items.</summary>
    public static CollectedItem Item(
        string collector, ItemKind kind, ItemRole role, Confidence confidence, string reason,
        string? path = null, RegistryLocation? registry = null, string? valueName = null, string? entry = null,
        SessionUser? user = null, string? warning = null, Dictionary<string, string>? details = null) => new()
    {
        Collector = collector,
        Kind = kind,
        Role = role,
        Confidence = confidence,
        Reason = reason,
        Path = path is null ? null : WinPath.Normalize(path),
        Registry = registry,
        ValueName = valueName,
        Entry = entry,
        UserSid = user?.Sid ?? registry?.UserSid,
        Warning = warning,
        Details = details,
    };
}

/// <summary>An MSI product installed on the machine.</summary>
public sealed record MsiProduct(MsiProductRegistration Registration, IReadOnlyList<MsiComponent> Components)
{
    /// <summary>Per-machine installs are registered under the LocalSystem SID.</summary>
    public bool PerMachine => Registration.UserDataSid == "S-1-5-18";
}

/// <summary>A folder of the application and how sure we are that it belongs to it.</summary>
public sealed record ApplicationFolder(string Path, Confidence Confidence);

/// <summary>An install directory and why it was retained.</summary>
public sealed record InstallRoot(string Path, Confidence Confidence, string Reason, bool IsPrimary);

public enum CollectionPhase
{
    /// <summary>Runs first, from the install directories and the application names.</summary>
    Main,
    /// <summary>Runs after the main phase, using its results (data folders, COM classes).</summary>
    Dependent,
}

/// <summary>A collector finds the items of one category.</summary>
public interface ICollector
{
    string Name { get; }
    CollectionPhase Phase => CollectionPhase.Main;
    IEnumerable<CollectedItem> Collect(CollectionContext context);
}
