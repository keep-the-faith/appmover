using System.Text.RegularExpressions;
using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Pe;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Text;
using AppMover.Inventory;
using AppMover.Packaging;

namespace AppMover.Deployment;

/// <summary>Step 5 (dry run): computes the operations needed to deploy a package on this machine.</summary>
public sealed class DeploymentPlanner(IRegistrySource registry, IWritableFileSystem files, SystemServices system)
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";
    private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private static readonly string[] BuiltInAccounts = ["LocalSystem", @"NT AUTHORITY\LocalService", @"NT AUTHORITY\NetworkService"];

    public DeploymentPlan Plan(PackageReader package, CollectionSession target, DeployOptions options)
    {
        var manifest = package.Manifest;
        var issues = package.Validate(verifyHashes: false);
        var id = DeploymentStore.NewId(manifest.App.KeyName);

        var userSids = MapUsers(manifest, target, options, issues);
        var (targetMap, installDir) = BuildTargetMap(manifest, target, options, userSids);
        var sourceMap = new TokenMap(manifest.Folders);
        var sourceTokenizer = new PathTokenizer(manifest.Folders.Select(f => (f.Key, f.Value)));
        string Relocate(string sourceText) => targetMap.ExpandText(sourceTokenizer.TokenizeText(sourceText));

        CheckAlreadyInstalled(manifest.App, target, userSids, options, issues);
        CheckPrerequisites(manifest, target, issues);

        var operations = new List<DeployOperation>();
        var skipped = 0;
        var temporary = 0; // collected by older versions
        var virtualStore = VirtualStoreRelocation(manifest.InstallDir, installDir);

        foreach (var directory in manifest.Directories)
        {
            if (targetMap.ExpandPath(virtualStore(directory)) is not { } path)
                skipped++;
            else if (target.IsTemporary(path))
                temporary++;
            else
                operations.Add(new CreateDirectoryOperation(path));
        }

        var taskFiles = manifest.Tasks.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shortcuts = manifest.Shortcuts.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files.Where(f => !taskFiles.Contains(f.Path)))
        {
            if (targetMap.ExpandPath(virtualStore(file.Path)) is not { } path)
            {
                skipped++;
                continue;
            }
            if (target.IsTemporary(path))
            {
                temporary++;
                continue;
            }
            if (shortcuts.TryGetValue(file.Path, out var shortcut) && Retarget(shortcut, sourceMap, targetMap) is { } retargeted)
                operations.Add(new CreateShortcutOperation(path, retargeted));
            else if (NewerOnTarget(file, path) is { } newer)
                issues.Add(new PackageIssue(IssueSeverity.Warning,
                    $"Kept the newer file already installed (version {newer} on this PC, {file.FileVersion} in the package).", path));
            else
                operations.Add(new ExtractFileOperation(file, path));
        }
        if (skipped > 0)
            issues.Add(new PackageIssue(IssueSeverity.Warning, $"{skipped} file(s)/folder(s) of unmapped users skipped."));
        if (temporary > 0)
            issues.Add(new PackageIssue(IssueSeverity.Warning, $"{temporary} temporary file(s)/folder(s) skipped (Temp folders)."));
        if (manifest.Shortcuts.Any(s => s.Advertised))
            issues.Add(new PackageIssue(IssueSeverity.Warning,
                "Windows Installer advertised shortcuts are copied as is: they may not start the application without its MSI registration."));

        operations.AddRange(PlanServices(package, targetMap, issues));
        operations.AddRange(PlanRegistry(package, targetMap, userSids, issues));
        operations.AddRange(PlanPathEntries(manifest, targetMap, userSids));
        operations.AddRange(PlanTasks(package, Relocate, userSids, options, issues));

        if (UninstallLocation(manifest.App, userSids) is { } uninstallKey)
            operations.Add(new WriteUninstallEntryOperation(uninstallKey, $"{options.UninstallCommand} {id}"));

        var bytes = operations.OfType<ExtractFileOperation>().Sum(o => o.File.Size);
        if (installDir is not null && files.GetAvailableSpace(installDir) is { } available && available < bytes)
            issues.Add(new PackageIssue(IssueSeverity.Error, $"Not enough disk space: {bytes / 1048576} MB needed, {available / 1048576} MB available.", installDir));

        return new DeploymentPlan
        {
            Id = id,
            Manifest = manifest,
            InstallDir = installDir,
            Target = targetMap,
            UserSids = userSids.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Operations = operations,
            Issues = issues,
            RelocateText = Relocate,
        };
    }

    /// <summary>
    /// The UAC VirtualStore mirrors the install folder path without its drive
    /// (<c>{U0.LocalAppData}\VirtualStore\Program Files\Acme</c>): it follows the install folder when it moves.
    /// </summary>
    private static Func<string, string> VirtualStoreRelocation(string? sourceInstallDir, string? targetInstallDir)
    {
        if (sourceInstallDir is not { Length: > 3 } source || targetInstallDir is not { Length: > 3 } target
            || source[1] != ':' || target[1] != ':' || source[3..].Equals(target[3..], StringComparison.OrdinalIgnoreCase))
            return path => path;

        var pattern = new Regex(@"^(\{U\d+\.LocalAppData\}\\VirtualStore\\)" + Regex.Escape(WinPath.Normalize(source)[3..]) + @"(?=\\|$)",
            RegexOptions.IgnoreCase);
        var replacement = "${1}" + WinPath.Normalize(target)[3..].Replace("$", "$$");
        return path => pattern.Replace(path, replacement);
    }

    /// <summary>Source user index → target SID, for the users present in the target session.</summary>
    private static Dictionary<int, string> MapUsers(PackageManifest manifest, CollectionSession target, DeployOptions options, List<PackageIssue> issues)
    {
        var map = new Dictionary<int, string>();
        foreach (var user in manifest.Users)
        {
            if (options.UserMap.TryGetValue(user.Index, out var sid) && target.FindUser(sid) is not null)
                map[user.Index] = sid;
            else
                issues.Add(new PackageIssue(IssueSeverity.Warning, $"Source user '{user.Name}' is not mapped to a user of this machine: their files and settings are skipped."));
        }
        return map;
    }

    /// <summary>Target folders for every token; {InstallDir} keeps its place relative to the known folders.</summary>
    private static (TokenMap Map, string? InstallDir) BuildTargetMap(
        PackageManifest manifest, CollectionSession target, DeployOptions options, Dictionary<int, string> userSids)
    {
        var folders = target.Machine.All().ToDictionary(f => f.Token, f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var (index, sid) in userSids)
        {
            foreach (var (token, path) in target.FindUser(sid)!.Folders.All())
                folders[$"U{index}.{token}"] = path;
        }

        var installDir = options.InstallDir;
        if (installDir is null && manifest.InstallDir is not null)
        {
            // "C:\Program Files\Acme" → {ProgramFiles}\Acme → wherever Program Files is on the target.
            var sourceFolders = manifest.Folders.Where(f => f.Key != PathTokenizer.InstallDirToken).Select(f => (f.Key, f.Value));
            var tokenized = new PathTokenizer(sourceFolders).Tokenize(manifest.InstallDir);
            installDir = new TokenMap(folders).ExpandPath(tokenized) ?? manifest.InstallDir;
        }
        if (installDir is not null)
            folders[PathTokenizer.InstallDirToken] = WinPath.Normalize(installDir);

        return (new TokenMap(folders), installDir is null ? null : WinPath.Normalize(installDir));
    }

    private void CheckAlreadyInstalled(InstalledApp app, CollectionSession target, Dictionary<int, string> userSids, DeployOptions options,
        List<PackageIssue> issues)
    {
        if (UninstallLocation(app, userSids) is not { } location)
            return;
        using var key = target.OpenKey(registry, location);
        if (key is null)
            return;
        issues.Add(options.Force
            ? new PackageIssue(IssueSeverity.Warning, "The application is already installed: it will be overwritten.", location.ToString())
            : new PackageIssue(IssueSeverity.Error, "The application is already installed (use --force to overwrite it).", location.ToString()));
    }

    /// <summary>
    /// Like Windows Installer: a versioned file (executable, DLL) is not replaced by an older one. Returns the version of
    /// the file already on the target when it is newer than the packaged one, null otherwise.
    /// </summary>
    private Version? NewerOnTarget(PackageFile file, string path)
    {
        if (!Version.TryParse(file.FileVersion, out var packaged) || !files.FileExists(path))
            return null;
        try
        {
            using var stream = files.OpenRead(path);
            return PeReader.Read(stream)?.FileVersion is { } installed && installed > packaged ? installed : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // unreadable: replaced (and backed up) as before
        }
    }

    /// <summary>Prerequisites are not installed by AppMover: missing ones are reported.</summary>
    private void CheckPrerequisites(PackageManifest manifest, CollectionSession target, List<PackageIssue> issues)
    {
        var checker = new PrerequisiteChecker(registry, files, target.Machine);
        foreach (var prerequisite in manifest.Prerequisites)
        {
            switch (checker.IsInstalled(prerequisite))
            {
                case false:
                    issues.Add(new PackageIssue(IssueSeverity.Warning,
                        $"Missing prerequisite: {prerequisite.Name}. Install it, otherwise the application may not start.", prerequisite.Reason));
                    break;
                case null:
                    issues.Add(new PackageIssue(IssueSeverity.Warning, $"Prerequisite not checked: {prerequisite.Name}.", prerequisite.Reason));
                    break;
            }
        }
    }

    /// <summary>Uninstall key of the application on the target (per-user apps go to the first mapped user).</summary>
    private static RegistryLocation? UninstallLocation(InstalledApp app, Dictionary<int, string> userSids)
    {
        var path = $@"{InventoryService.UninstallPath}\{app.KeyName}";
        if (app.Scope == AppScope.Machine)
            return RegistryLocation.Machine(app.View == RegView.Registry32 ? RegView.Registry32 : RegView.Registry64, path);
        return userSids.Count == 0 ? null : RegistryLocation.User(userSids.OrderBy(u => u.Key).First().Value, path);
    }

    /// <summary>The shortcut to recreate when one of its paths changes on the target, null when it can be copied as is.</summary>
    private static ShortcutTarget? Retarget(ShortcutRecord shortcut, TokenMap source, TokenMap target)
    {
        if (shortcut.Advertised || shortcut.Target is null)
            return null;

        string? Expand(TokenMap map, string? text) => text is null ? null : map.ExpandText(text);
        var fields = new[] { shortcut.Target, shortcut.Arguments, shortcut.WorkingDirectory, shortcut.IconLocation };
        var unchanged = fields.All(f => string.Equals(Expand(source, f), Expand(target, f), StringComparison.OrdinalIgnoreCase));
        return unchanged
            ? null
            : new ShortcutTarget(target.ExpandText(shortcut.Target), Expand(target, shortcut.Arguments), Expand(target, shortcut.WorkingDirectory),
                Expand(target, shortcut.IconLocation), shortcut.Description);
    }

    private IEnumerable<DeployOperation> PlanServices(PackageReader package, TokenMap targetMap, List<PackageIssue> issues)
    {
        foreach (var name in package.Manifest.Services)
        {
            var record = package.Registry.FirstOrDefault(r => r.Hive == RegHive.LocalMachine && !r.Partial
                && r.Path.Equals($@"{ServicesPath}\{name}", StringComparison.OrdinalIgnoreCase));
            if (record is null)
                continue;
            if (system.Services.Exists(name))
            {
                issues.Add(new PackageIssue(IssueSeverity.Warning, $"Service '{name}' already exists: only its settings are updated."));
                continue;
            }

            RegValue? Value(string valueName) => record.Values.FirstOrDefault(v => v.Name.Equals(valueName, StringComparison.OrdinalIgnoreCase));
            uint Number(string valueName, uint fallback) => (uint)(Value(valueName)?.AsNumber() ?? fallback);

            var account = Value("ObjectName")?.AsString();
            if (account is not null && !BuiltInAccounts.Contains(account, StringComparer.OrdinalIgnoreCase)
                && !account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase))
                issues.Add(new PackageIssue(IssueSeverity.Warning, $"Service '{name}' runs as '{account}': its password must be set again (services.msc)."));

            var imagePath = Value("ImagePath")?.AsString();
            if (imagePath is null)
                continue;
            yield return new CreateServiceOperation(new ServiceSpec(
                name,
                targetMap.ExpandText(imagePath),
                Number("Type", 0x10),
                Number("Start", 3),
                Number("ErrorControl", 1),
                Value("DisplayName")?.AsString(),
                account is null || account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ? null : account,
                Value("DependOnService")?.Multi ?? []));
        }
    }

    private static IEnumerable<DeployOperation> PlanRegistry(PackageReader package, TokenMap targetMap, Dictionary<int, string> userSids,
        List<PackageIssue> issues)
    {
        var drivers = package.Manifest.Items
            .Where(i => i.Selected && i.Role == ItemRole.Driver && i.Registry is not null)
            .Select(i => i.Registry!.Path)
            .ToList();
        var skippedDrivers = 0;
        var firewall = false;

        foreach (var record in package.Registry)
        {
            if (drivers.Any(d => WinPath.IsUnderOrEqual(record.Path, d)))
            {
                skippedDrivers++;
                continue;
            }

            RegistryLocation location;
            if (record.Hive == RegHive.Users)
            {
                if (record.UserIndex is not { } index || !userSids.TryGetValue(index, out var sid))
                    continue; // already reported as an unmapped user
                location = RegistryLocation.User(sid, record.Path);
            }
            else
            {
                location = RegistryLocation.Machine(record.View == RegView.Registry32 ? RegView.Registry32 : RegView.Registry64, record.Path);
            }

            var tokenized = new HashSet<string>(record.TokenizedValues ?? [], StringComparer.OrdinalIgnoreCase);
            var values = record.Values.Select(v => tokenized.Contains(v.Name) ? Expand(v, targetMap) : v).ToList();
            firewall |= record.Path.Contains("FirewallPolicy", StringComparison.OrdinalIgnoreCase);
            yield return new WriteRegistryOperation(location, values, record.Partial);
        }

        if (skippedDrivers > 0)
            issues.Add(new PackageIssue(IssueSeverity.Warning, "Drivers are not deployed: install them with their own setup or pnputil."));
        if (firewall)
            issues.Add(new PackageIssue(IssueSeverity.Warning, "Firewall rules take effect after the firewall service restarts (or a reboot)."));
    }

    private static RegValue Expand(RegValue value, TokenMap map) => value.Type switch
    {
        RegValueType.String or RegValueType.ExpandString when value.Text is not null => value with { Text = map.ExpandText(value.Text) },
        RegValueType.MultiString when value.Multi is not null => value with { Multi = value.Multi.Select(map.ExpandText).ToArray() },
        _ => value,
    };

    private static IEnumerable<DeployOperation> PlanPathEntries(PackageManifest manifest, TokenMap targetMap, Dictionary<int, string> userSids)
    {
        foreach (var entry in manifest.PathEntries)
        {
            RegistryLocation location;
            if (entry.Hive == RegHive.Users)
            {
                if (entry.UserIndex is not { } index || !userSids.TryGetValue(index, out var sid))
                    continue;
                location = RegistryLocation.User(sid, "Environment");
            }
            else
            {
                location = RegistryLocation.Machine(RegView.Registry64, MachineEnvironment);
            }
            yield return new AddPathEntryOperation(location, entry.Variable, targetMap.ExpandText(entry.Entry));
        }
    }

    private IEnumerable<DeployOperation> PlanTasks(PackageReader package, Func<string, string> relocate,
        Dictionary<int, string> userSids, DeployOptions options, List<PackageIssue> issues)
    {
        var manifest = package.Manifest;
        // Accounts of the source users, as they may appear in a task definition.
        var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in manifest.Users)
        {
            if (!userSids.TryGetValue(user.Index, out var sid))
                continue;
            accounts[user.Sid] = sid;
            accounts[user.Name] = sid;
            accounts[$@"{manifest.Source.ComputerName}\{user.Name}"] = sid;
        }

        foreach (var task in manifest.Tasks)
        {
            var file = manifest.Files.FirstOrDefault(f => f.Path.Equals(task.Path, StringComparison.OrdinalIgnoreCase));
            if (file is null)
                continue;
            if (system.Tasks.Exists(task.Name) && !options.Force)
            {
                issues.Add(new PackageIssue(IssueSeverity.Warning, $"Scheduled task '{task.Name}' already exists: kept (use --force to replace it)."));
                continue;
            }

            string xml;
            using (var reader = new StreamReader(package.OpenEntry(file.Entry), detectEncodingFromByteOrderMarks: true))
                xml = reader.ReadToEnd();
            yield return new RegisterTaskOperation(task.Name, TaskXmlRewriter.Rewrite(xml, relocate, accounts));
        }
    }
}
