using AppMover.Core.Models;
using AppMover.Deployment;
using AppMover.Host;
using AppMover.Packaging;

namespace AppMover.Cli;

/// <summary>verify, deploy, deployments, uninstall.</summary>
internal sealed partial class Commands
{
    private int Verify(CliArguments cli)
    {
        if (CheckOptions(cli, "verbose") is { } error)
            return error;
        if (cli.Positionals.Count != 1)
            return Usage("verify expects the package path.");

        using var package = OpenPackage(cli.Positionals[0]);
        PrintPackage(package.Manifest);
        var issues = package.Validate(verifyHashes: true);
        foreach (var issue in issues)
            Console.WriteLine($"  [{issue.Severity}] {issue.Message}{(issue.Location is null ? "" : $" ({issue.Location})")}");
        Console.WriteLine(issues.Count == 0 ? "Package OK." : $"{issues.Count} problem(s).");
        return issues.Any(i => i.Severity == IssueSeverity.Error) ? ExitCodes.Error : ExitCodes.Ok;
    }

    private int Deploy(CliArguments cli)
    {
        if (CheckOptions(cli, "verbose", "install-dir", "user-map", "dry-run", "force", "yes") is { } error)
            return error;
        if (cli.Positionals.Count != 1)
            return Usage("deploy expects the package path.");

        using var package = OpenPackage(Path.GetFullPath(cli.Positionals[0]));
        PrintPackage(package.Manifest);

        var profiles = _host.GetProfiles();
        if (MapUsers(cli.Get("user-map"), package.Manifest.Users, profiles) is not { } userMap)
            return ExitCodes.Usage;

        using var session = _host.OpenSession(userMap.Values.Distinct());
        var installDir = cli.Get("install-dir") is { } dir ? Path.GetFullPath(dir) : null;
        var plan = _host.PlanDeployment(package, session, installDir, userMap, cli.Has("force"));

        PrintPlan(plan, userMap, profiles, cli.Has("dry-run"));
        if (cli.Has("dry-run"))
            return plan.CanProceed ? ExitCodes.Ok : ExitCodes.Error;
        if (!plan.CanProceed)
        {
            Console.Error.WriteLine("Deployment impossible: fix the errors above.");
            return ExitCodes.Error;
        }
        if (!cli.Has("yes") && !Confirm($"Deploy {package.Manifest.App.DisplayName} on this machine?"))
            return ExitCodes.Ok;

        try
        {
            var progress = cli.Has("verbose") ? new Progress<string>(m => Console.WriteLine("  " + m)) : null;
            var record = _host.Deploy(plan, package, session, progress);
            Console.WriteLine($"Deployed. Id: {record.Id} ({record.Journal.Count} change(s)).");
            foreach (var warning in record.Warnings)
                Console.WriteLine($"Warning: {warning}");
            Console.WriteLine($"Uninstall from \"Apps & features\" or with: appmover uninstall {record.Id}");
            return ExitCodes.Ok;
        }
        catch (DeploymentException e)
        {
            Console.Error.WriteLine(e.Message);
            foreach (var failure in e.RollbackFailures)
                Console.Error.WriteLine($"  Not reverted: {failure}");
            return ExitCodes.Error;
        }
    }

    private int ListDeployments(CliArguments cli)
    {
        if (CheckOptions(cli, "verbose", "json") is { } error)
            return error;

        var records = _host.Store.List();
        if (cli.Has("json"))
        {
            Output.WriteJson(records);
            return ExitCodes.Ok;
        }
        Output.WriteTable(
            [("Id", 50), ("Application", 40), ("Version", 16), ("Deployed (UTC)", 16), ("State", 11), ("Install directory", 60)],
            records.Select(r => new[] { r.Id, r.AppName, r.AppVersion, r.DeployedUtc.ToString("yyyy-MM-dd HH:mm"), r.State.ToString(), r.InstallDir }));
        return ExitCodes.Ok;
    }

    private int Uninstall(CliArguments cli)
    {
        if (CheckOptions(cli, "verbose", "yes") is { } error)
            return error;
        if (cli.Positionals.Count != 1)
            return Usage("uninstall expects a deployment id or an application name.");

        var query = cli.Positionals[0];
        var matches = _host.FindDeployments(query);
        if (matches.Count != 1)
        {
            Console.Error.WriteLine(matches.Count == 0 ? $"No deployment matches '{query}'." : $"'{query}' matches {matches.Count} deployments, use the id.");
            foreach (var match in matches)
                Console.Error.WriteLine($"  {match.Id}  ({match.AppName})");
            return ExitCodes.NotFound;
        }

        var record = matches[0];
        if (!cli.Has("yes") && !Confirm($"Uninstall {record.AppName} ({record.Journal.Count} change(s) to revert)?"))
            return ExitCodes.Ok;

        var failures = _host.Uninstall(record);
        if (failures.Count == 0)
        {
            Console.WriteLine($"{record.AppName} uninstalled.");
            return ExitCodes.Ok;
        }
        Console.Error.WriteLine($"{failures.Count} change(s) could not be reverted (run the command again once the cause is fixed):");
        foreach (var failure in failures)
            Console.Error.WriteLine($"  {failure}");
        return ExitCodes.Error;
    }

    /// <summary>Opens a package, asking for its password when it is encrypted.</summary>
    private static PackageReader OpenPackage(string path)
    {
        if (!PackageCrypto.IsEncrypted(path))
            return AppMoverHost.OpenPackage(path);
        var password = Passwords.Read($"Password of {Path.GetFileName(path)}: ")
                       ?? throw new PackageEncryptedException(path);
        return AppMoverHost.OpenPackage(path, password);
    }

    /// <summary>
    /// Default map, then "0=alice,1=S-1-5-21-…,2=skip" overrides. Returns null (after printing why) when the option is invalid.
    /// </summary>
    private Dictionary<int, string>? MapUsers(string? option, IReadOnlyList<PackageUser> sourceUsers, IReadOnlyList<UserProfile> profiles)
    {
        var map = _host.DefaultUserMap(sourceUsers, profiles);
        foreach (var pair in (option ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var index) || sourceUsers.All(u => u.Index != index))
            {
                Console.Error.WriteLine($"Invalid user mapping '{pair}' (expected <source index>=<user name|SID>, see the package users).");
                return null;
            }
            if (parts[1] is "-" or "skip")
            {
                map.Remove(index);
                continue;
            }
            if (AppMoverHost.FindProfileSid(profiles, parts[1]) is not { } sid)
            {
                Console.Error.WriteLine($"Unknown user '{parts[1]}' on this machine.");
                return null;
            }
            map[index] = sid;
        }
        return map;
    }

    private static void PrintPackage(PackageManifest manifest)
    {
        Console.WriteLine($"Package : {manifest.App.DisplayName} {manifest.App.DisplayVersion} ({manifest.App.Installer?.Type})");
        Console.WriteLine($"Source  : {manifest.Source.ComputerName}, {manifest.Source.OsDescription}, {manifest.CreatedUtc:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"Content : {manifest.Files.Count} file(s), {FormatSize(manifest.Files.Sum(f => f.Size))}, " +
                          $"{manifest.Services.Count} service(s), {manifest.Tasks.Count} task(s), {manifest.Shortcuts.Count} shortcut(s)");
        foreach (var user in manifest.Users)
            Console.WriteLine($"User {user.Index}  : {user.Name} ({user.Sid})");
        Console.WriteLine();
    }

    private static void PrintPlan(DeploymentPlan plan, Dictionary<int, string> userMap, IReadOnlyList<UserProfile> profiles, bool details)
    {
        Console.WriteLine($"Install directory: {plan.InstallDir ?? "(none)"}");
        foreach (var user in plan.Manifest.Users)
        {
            var target = userMap.TryGetValue(user.Index, out var sid) ? $"{AppMoverHost.ProfileName(profiles, sid)} ({sid})" : "skipped";
            Console.WriteLine($"User {user.Index} ({user.Name}) → {target}");
        }
        Console.WriteLine($"Operations: {plan.Operations.Count} ({FormatSize(plan.BytesToWrite)} to write)");
        foreach (var group in plan.Operations.GroupBy(o => o.GetType().Name.Replace("Operation", "")))
            Console.WriteLine($"  {group.Key,-20} {group.Count()}");
        if (details)
        {
            Console.WriteLine();
            foreach (var operation in plan.Operations)
                Console.WriteLine("  " + operation.Describe());
        }
        Console.WriteLine();
        foreach (var issue in plan.Issues)
            Console.WriteLine($"[{issue.Severity}] {issue.Message}{(issue.Location is null ? "" : $" ({issue.Location})")}");
        if (plan.Issues.Count > 0)
            Console.WriteLine();
    }

    private static bool Confirm(string question)
    {
        Console.Write($"{question} [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                                      || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
