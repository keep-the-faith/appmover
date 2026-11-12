using AppMover.Core.Models;
using AppMover.Core.Registry;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// Registry entries outside the application's own keys that point into its install directory:
/// startup entries, App Paths, environment variables, firewall rules, event log sources.
/// </summary>
public sealed class SystemRegistryCollector : ICollector
{
    private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string FirewallRules = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
    private const string EventLog = @"SYSTEM\CurrentControlSet\Services\EventLog";

    /// <summary>Environment variables holding a list of folders.</summary>
    private static readonly string[] ListVariables = ["Path", "PSModulePath", "CLASSPATH"];

    public string Name => "System registry";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        if (context.InstallRoots.Count == 0)
            return [];

        return CollectStartup(context)
            .Concat(CollectAppPaths(context))
            .Concat(CollectEnvironment(context))
            .Concat(CollectFirewall(context))
            .Concat(CollectEventLog(context));
    }

    private IEnumerable<CollectedItem> CollectStartup(CollectionContext context)
    {
        foreach (var keyName in new[] { "Run", "RunOnce" })
        {
            foreach (var scope in context.SoftwareScopes($@"Microsoft\Windows\CurrentVersion\{keyName}"))
            {
                using var key = context.Open(scope.Location);
                if (key is null)
                    continue;
                foreach (var valueName in key.GetValueNames())
                {
                    if (context.FindReferencedRoot(key.GetString(valueName), scope.User) is { } root)
                        yield return Item(Name, ItemKind.RegistryValue, ItemRole.Startup, Confidence.Certain,
                            $"Started with Windows from {root}", registry: scope.Location, valueName: valueName);
                }
            }
        }
    }

    private IEnumerable<CollectedItem> CollectAppPaths(CollectionContext context)
    {
        foreach (var scope in context.SoftwareScopes(@"Microsoft\Windows\CurrentVersion\App Paths"))
        {
            using var appPaths = context.Open(scope.Location);
            if (appPaths is null)
                continue;
            foreach (var exe in appPaths.GetSubKeyNames())
            {
                using var key = appPaths.OpenSubKey(exe);
                var root = context.FindReferencedRoot(key?.GetString(""), scope.User) ?? context.FindReferencedRoot(key?.GetString("Path"), scope.User);
                if (root is not null)
                    yield return Item(Name, ItemKind.RegistryKey, ItemRole.AppPath, Confidence.Certain, $"App Path of {exe}",
                        registry: scope.Location with { Path = $@"{scope.Location.Path}\{exe}" });
            }
        }
    }

    private IEnumerable<CollectedItem> CollectEnvironment(CollectionContext context)
    {
        var scopes = new List<RegistryScope> { new(RegistryLocation.Machine(RegView.Registry64, MachineEnvironment), null) };
        scopes.AddRange(context.Session.Users.Select(u => new RegistryScope(RegistryLocation.User(u.Sid, "Environment"), u)));

        foreach (var scope in scopes)
        {
            using var key = context.Open(scope.Location);
            if (key is null)
                continue;
            foreach (var name in key.GetValueNames())
            {
                var value = key.GetString(name);
                if (value is null)
                    continue;

                if (ListVariables.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    // Only the entries of the application, never the whole variable.
                    foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (context.FindReferencedRoot(entry, scope.User) is { } root)
                            yield return Item(Name, ItemKind.PathEntry, ItemRole.Environment, Confidence.Certain,
                                $"Entry of %{name}% in {root}", registry: scope.Location, valueName: name, entry: entry);
                    }
                }
                else if (context.FindReferencedRoot(value, scope.User) is { } root)
                {
                    yield return Item(Name, ItemKind.RegistryValue, ItemRole.Environment, Confidence.Certain,
                        $"Environment variable %{name}% points to {root}", registry: scope.Location, valueName: name);
                }
            }
        }
    }

    private IEnumerable<CollectedItem> CollectFirewall(CollectionContext context)
    {
        var location = RegistryLocation.Machine(RegView.Registry64, FirewallRules);
        using var rules = context.Open(location);
        if (rules is null)
            yield break;

        foreach (var name in rules.GetValueNames())
        {
            // "v2.30|Action=Allow|Active=TRUE|Dir=In|Protocol=6|App=C:\App\app.exe|Name=App|"
            var app = rules.GetString(name)?.Split('|')
                .FirstOrDefault(f => f.StartsWith("App=", StringComparison.OrdinalIgnoreCase))?[4..];
            if (context.FindReferencedRoot(app) is not null)
                yield return Item(Name, ItemKind.RegistryValue, ItemRole.FirewallRule, Confidence.Certain,
                    $"Firewall rule for {app}", registry: location, valueName: name);
        }
    }

    private IEnumerable<CollectedItem> CollectEventLog(CollectionContext context)
    {
        using var eventLog = context.Open(RegistryLocation.Machine(RegView.Registry64, EventLog));
        if (eventLog is null)
            yield break;

        foreach (var log in eventLog.GetSubKeyNames())
        {
            using var logKey = eventLog.OpenSubKey(log);
            if (logKey is null)
                continue;
            foreach (var source in logKey.GetSubKeyNames())
            {
                using var sourceKey = logKey.OpenSubKey(source);
                if (context.FindReferencedRoot(sourceKey?.GetString("EventMessageFile")) is { } root)
                    yield return Item(Name, ItemKind.RegistryKey, ItemRole.Settings, Confidence.Probable,
                        $"Event log source '{source}' uses messages from {root}",
                        registry: RegistryLocation.Machine(RegView.Registry64, $@"{EventLog}\{log}\{source}"));
            }
        }
    }
}
