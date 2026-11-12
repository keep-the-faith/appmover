using System.Runtime.Versioning;
using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Msi;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Tracing;
using AppMover.Deployment;
using AppMover.Deployment.Windows;
using AppMover.Detection;
using AppMover.Inventory;
using AppMover.Packaging;
using Microsoft.Extensions.Logging;

[assembly: SupportedOSPlatform("windows")]

namespace AppMover.Host;

/// <param name="Package">The package written.</param>
/// <param name="FilesFromSnapshot">Locked files that were read from a Volume Shadow Copy.</param>
public sealed record ExportResult(PackageResult Package, int FilesFromSnapshot);

/// <summary>
/// Windows composition root shared by the command line and the graphical interface:
/// every step of AppMover with the real registry, file system and system services.
/// </summary>
public sealed class AppMoverHost(ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AppMoverHost>();
    private bool _hivePrivileges;

    public WindowsRegistrySource Registry { get; } = new();
    public SystemPathResolver Paths { get; } = new();
    public PhysicalFileSystem Files { get; } = new();

    /// <summary>Journals and backups of the deployments made on this machine.</summary>
    public static string DeploymentsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppMover", "Deployments");

    /// <summary>
    /// Stable copy of the running executable (appmover.exe or appmover-gui.exe), called by the uninstall entries of the
    /// deployments: each one handles "uninstall &lt;id&gt;".
    /// </summary>
    public static string InstalledExecutable =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AppMover",
            Path.GetFileName(Environment.ProcessPath) ?? "appmover.exe");

    public DeploymentStore Store => new(Files, DeploymentsRoot);

    public string? CurrentUserSid => Privileges.CurrentUserSid();

    public static bool IsElevated => Privileges.IsElevated();

    public IReadOnlyList<UserProfile> GetProfiles() => new UserProfileProvider(Registry, Paths).GetProfiles();

    // ---- Steps 1-2: inventory and detection

    public IReadOnlyList<InstalledApp> LoadApps(bool allUsers, bool includeHidden)
    {
        if (allUsers)
            EnableHivePrivileges();
        var inventory = new InventoryService(Registry, new UserProfileProvider(Registry, Paths), new WindowsHiveMounter(), CurrentUserSid,
            loggerFactory.CreateLogger<InventoryService>());
        var apps = inventory.GetInstalledApps(new InventoryOptions { AllUsers = allUsers, IncludeHidden = includeHidden });
        new InstallerDetector(new DetectionContext(Registry, Files, Paths), loggerFactory.CreateLogger<InstallerDetector>()).DetectAll(apps);
        return apps;
    }

    /// <summary>Exact id, then exact key name, then exact display name, then part of the display name.</summary>
    public static List<InstalledApp> FindApps(IReadOnlyList<InstalledApp> apps, string query)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        IEnumerable<Func<InstalledApp, bool>> strategies =
        [
            a => a.Id.Equals(query, comparison),
            a => a.KeyName.Equals(query, comparison),
            a => string.Equals(a.DisplayName, query, comparison),
            a => a.DisplayName?.Contains(query, comparison) == true,
        ];
        foreach (var strategy in strategies)
        {
            var matches = apps.Where(strategy).ToList();
            if (matches.Count > 0)
                return matches;
        }
        return [];
    }

    // ---- Steps 3-4: collection and package

    /// <summary>The users whose data is collected: the current one and the owner of a per-user app, or everybody.</summary>
    public CollectionSession OpenCollectionSession(InstalledApp app, bool allUsers)
    {
        var sids = allUsers
            ? GetProfiles().Select(p => p.Sid).ToList()
            : new[] { CurrentUserSid, app.UserSid }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return OpenSession(sids);
    }

    /// <summary>Opens a session for these users; the hives of those who are not logged on are mounted until it is disposed.</summary>
    public CollectionSession OpenSession(IEnumerable<string> userSids)
    {
        EnableHivePrivileges();
        return CollectionSession.Open(new UserProfileProvider(Registry, Paths), new WindowsHiveMounter(), new KnownFoldersResolver(Registry, Paths),
            userSids, _logger);
    }

    /// <summary>Step 3; <paramref name="trace"/> adds what the application used while it was traced.</summary>
    public CollectionResult Collect(InstalledApp app, CollectionSession session, UsageTrace? trace = null) =>
        new CollectionService(Registry, Files, Paths, loggerFactory.CreateLogger<CollectionService>(), new WindowsMsiDatabaseReader(),
            _nsisScripts ??= new SevenZipNsisScriptReader(_logger)).Collect(app, session, trace);

    private SevenZipNsisScriptReader? _nsisScripts;

    // ---- Dynamic analysis

    /// <summary>
    /// Starts recording what the application reads and writes (kernel ETW trace). Its running processes are followed;
    /// with <paramref name="launch"/>, its main executable is started too.
    /// </summary>
    public IUsageTraceSession StartUsageTrace(InstalledApp app, CollectionResult collection, bool launch) =>
        new EtwUsageTracer(_logger).Start(app.Id, collection.InstallRoots.Select(r => r.Path).ToList(), launch ? MainExecutable(app, collection) : null);

    /// <summary>The executable of the application's icon, when it is in its install folder.</summary>
    public static string? MainExecutable(InstalledApp app, CollectionResult collection)
    {
        if (app.DisplayIcon is null)
            return null;
        var icon = Environment.ExpandEnvironmentVariables(InstallRootResolver.StripIconIndex(app.DisplayIcon));
        return icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon)
               && collection.InstallRoots.Any(r => Core.Text.WinPath.IsUnderOrEqual(icon, r.Path))
            ? icon
            : null;
    }

    /// <summary>
    /// Writes the package. Files locked by a running program are read from a Volume Shadow Copy unless
    /// <paramref name="useSnapshots"/> is false; <paramref name="password"/> encrypts the package.
    /// </summary>
    public ExportResult Export(string path, InstalledApp app, CollectionResult collection, IReadOnlyList<CollectedItem> items,
        CollectionSession session, string? password = null, bool useSnapshots = true)
    {
        using var snapshots = useSnapshots ? new SnapshotFallbackFileSystem(Files, new WmiSnapshotProvider(), _logger) : null;
        var package = new PackageWriter(Registry, (IFileSystem?)snapshots ?? Files, loggerFactory.CreateLogger<PackageWriter>())
            .Write(path, app, collection, items, session, password);
        return new ExportResult(package, snapshots?.FilesFromSnapshot ?? 0);
    }

    /// <summary>Opens a package (decrypted with the password when it is encrypted).</summary>
    public static PackageReader OpenPackage(string path, string? password = null) => PackageReader.Open(path, password);

    /// <summary>"Acme_Editor_2.1.appmove".</summary>
    public static string DefaultPackageName(InstalledApp app)
    {
        var name = $"{app.DisplayName ?? app.KeyName}{(app.DisplayVersion is null ? "" : "_" + app.DisplayVersion)}";
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        return new string(name.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray()) + PackageManifest.Extension;
    }

    // ---- Step 5: deployment

    /// <summary>Each source user goes to the user of the same name on this machine, otherwise to the current user.</summary>
    public Dictionary<int, string> DefaultUserMap(IReadOnlyList<PackageUser> sourceUsers, IReadOnlyList<UserProfile> profiles)
    {
        var map = new Dictionary<int, string>();
        foreach (var user in sourceUsers)
        {
            if ((FindProfileSid(profiles, user.Name) ?? CurrentUserSid) is { } sid)
                map[user.Index] = sid;
        }
        return map;
    }

    /// <summary>SID of the profile with this SID or this folder name.</summary>
    public static string? FindProfileSid(IReadOnlyList<UserProfile> profiles, string nameOrSid) =>
        profiles.FirstOrDefault(p => p.Sid.Equals(nameOrSid, StringComparison.OrdinalIgnoreCase)
                                     || Path.GetFileName(p.ProfilePath).Equals(nameOrSid, StringComparison.OrdinalIgnoreCase))?.Sid;

    public static string ProfileName(IReadOnlyList<UserProfile> profiles, string sid) =>
        Path.GetFileName(profiles.FirstOrDefault(p => p.Sid.Equals(sid, StringComparison.OrdinalIgnoreCase))?.ProfilePath) ?? sid;

    public DeploymentPlan PlanDeployment(PackageReader package, CollectionSession target, string? installDir, IReadOnlyDictionary<int, string> userMap,
        bool force) =>
        new DeploymentPlanner(Registry, Files, WindowsSystem()).Plan(package, target, new DeployOptions
        {
            InstallDir = installDir,
            UserMap = userMap,
            Force = force,
            UninstallCommand = $"\"{InstalledExecutable}\" uninstall",
        });

    public DeploymentRecord Deploy(DeploymentPlan plan, PackageReader package, CollectionSession target, IProgress<string>? progress = null)
    {
        InstallSelf();
        return CreateExecutor().Deploy(plan, package, target, progress);
    }

    /// <summary>Reverts a deployment. Returns the changes that could not be reverted.</summary>
    public IReadOnlyList<string> Uninstall(DeploymentRecord record)
    {
        using var session = OpenSession(record.UserSids);
        return CreateExecutor().Uninstall(record, session);
    }

    /// <summary>Deployment with this id, or the only one whose application name contains the text.</summary>
    public IReadOnlyList<DeploymentRecord> FindDeployments(string idOrName)
    {
        var records = Store.List();
        var byId = records.Where(r => r.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byId.Count > 0 ? byId : records.Where(r => r.AppName.Contains(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static SystemServices WindowsSystem() =>
        new(new WindowsServiceManager(), new SchtasksScheduler(), new WindowsShortcutWriter(), new WindowsSystemNotifier());

    private DeploymentExecutor CreateExecutor() =>
        new(Registry, Registry, Files, WindowsSystem(), Store, loggerFactory.CreateLogger<DeploymentExecutor>());

    /// <summary>Copies AppMover to Program Files so that the uninstall entries keep working.</summary>
    private void InstallSelf()
    {
        var current = Environment.ProcessPath;
        if (current is null || string.Equals(Path.GetFullPath(current), InstalledExecutable, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstalledExecutable)!);
            File.Copy(current, InstalledExecutable, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by another AppMover (same version most of the time): keep the existing copy.
            _logger.LogWarning("Cannot update {Path}: {Message}", InstalledExecutable, e.Message);
        }
    }

    /// <summary>Backup/restore privileges are needed to load the hives of users who are not logged on.</summary>
    public void EnableHivePrivileges()
    {
        if (_hivePrivileges)
            return;
        try
        {
            Privileges.Enable(Privileges.Backup);
            Privileges.Enable(Privileges.Restore);
            _hivePrivileges = true;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Cannot enable backup/restore privileges, offline user hives will be skipped: {Message}", e.Message);
        }
    }
}
