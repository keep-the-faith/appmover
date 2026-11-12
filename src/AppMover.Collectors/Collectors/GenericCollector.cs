using AppMover.Core.Models;
using AppMover.Core.Text;
using AppMover.Inventory;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>Uninstall entry, install directories and installer leftovers needed to uninstall.</summary>
public sealed class GenericCollector : ICollector
{
    public string Name => "Generic";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        var app = context.App;
        var uninstallKey = app.Scope == AppScope.Machine
            ? RegistryLocation.Machine(app.View, $@"{InventoryService.UninstallPath}\{app.KeyName}")
            : RegistryLocation.User(app.UserSid!, $@"{InventoryService.UninstallPath}\{app.KeyName}");
        yield return Item(Name, ItemKind.RegistryKey, ItemRole.UninstallEntry, Confidence.Certain, "Uninstall entry", registry: uninstallKey);

        foreach (var root in context.InstallRoots)
            yield return Item(Name, ItemKind.Directory, ItemRole.Application, root.Confidence, root.Reason, path: root.Path);

        // InstallShield keeps its uninstaller apart from the application.
        if (app.Installer?.UninstallerPath is { } uninstaller
            && uninstaller.Contains(@"\InstallShield Installation Information\", StringComparison.OrdinalIgnoreCase)
            && WinPath.GetDirectoryName(uninstaller) is { } setupDir
            && context.Files.DirectoryExists(setupDir))
        {
            yield return Item(Name, ItemKind.Directory, ItemRole.UninstallEntry, Confidence.Probable, "InstallShield uninstaller", path: setupDir);
        }

        // Burn runs its cached bundle to modify or uninstall the application.
        if (app.Installer?.Properties.GetValueOrDefault(InstallerProperties.BundleCachePath) is { } bundle
            && WinPath.GetDirectoryName(bundle) is { } cacheDir
            && context.Files.DirectoryExists(cacheDir))
        {
            yield return Item(Name, ItemKind.Directory, ItemRole.UninstallEntry, Confidence.Probable, "Cached setup of the Burn bundle", path: cacheDir);
        }
    }
}
