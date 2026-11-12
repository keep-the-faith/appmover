using AppMover.Core.Models;
using AppMover.Core.Registry;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// Services and drivers whose executable (or svchost ServiceDll) is in a folder of the application:
/// install directory (Certain) or data folder found in the main phase, e.g. an updater in ProgramData (at most Probable).
/// </summary>
public sealed class ServiceCollector : ICollector
{
    private const string Services = @"SYSTEM\CurrentControlSet\Services";

    public string Name => "Services";

    public CollectionPhase Phase => CollectionPhase.Dependent;

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        using var services = context.Open(RegistryLocation.Machine(RegView.Registry64, Services));
        if (services is null)
            yield break;

        foreach (var name in services.GetSubKeyNames())
        {
            using var service = services.OpenSubKey(name);
            if (service is null)
                continue;
            using var parameters = service.OpenSubKey("Parameters");
            var folder = context.FindReferencedFolder(service.GetString("ImagePath"))
                         ?? context.FindReferencedFolder(parameters?.GetString("ServiceDll"));
            if (folder is null)
                continue;

            // SERVICE_KERNEL_DRIVER = 1, SERVICE_FILE_SYSTEM_DRIVER = 2.
            var isDriver = (service.GetValue("Type")?.AsNumber() ?? 0) is 1 or 2;
            yield return Item(Name, ItemKind.RegistryKey, isDriver ? ItemRole.Driver : ItemRole.Service, folder.Confidence,
                $"{(isDriver ? "Driver" : "Service")} '{name}' runs from {folder.Path}",
                registry: RegistryLocation.Machine(RegView.Registry64, $@"{Services}\{name}"),
                warning: isDriver ? "Drivers are not deployed automatically (signature and installation via pnputil required)." : null,
                details: new Dictionary<string, string> { ["ServiceName"] = name });
        }
    }
}
