using AppMover.Collectors.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Msi;
using AppMover.Core.Nsis;
using AppMover.Core.Tracing;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Text;
using Microsoft.Extensions.Logging;

namespace AppMover.Collectors;

/// <summary>Result of step 3.</summary>
public sealed record CollectionResult(IReadOnlyList<InstallRoot> InstallRoots, AppKeywords Keywords, IReadOnlyList<CollectedItem> Items)
{
    public string? PrimaryInstallRoot => InstallRoots.FirstOrDefault(r => r.IsPrimary)?.Path;

    /// <summary>Runtimes the application needs (Visual C++, .NET…), found in its install folders.</summary>
    public IReadOnlyList<Prerequisite> Prerequisites { get; init; } = [];
}

/// <summary>Step 3: runs the collectors and merges their results.</summary>
public sealed class CollectionService(
    IRegistrySource registry, IFileSystem files, IPathResolver paths, ILogger<CollectionService> logger,
    IMsiDatabaseReader? msiDatabase = null, INsisScriptReader? nsisScripts = null, IReadOnlyList<ICollector>? collectors = null)
{
    public static IReadOnlyList<ICollector> DefaultCollectors { get; } =
    [
        new GenericCollector(),
        new MsiCollector(),
        new InnoSetupCollector(),
        new NsisCollector(),
        new InstallerLogCollector(),
        new UsageTraceCollector(),
        new ShortcutCollector(),
        new SystemRegistryCollector(),
        new ClassesCollector(),
        new HeuristicDataCollector(),
        new ServiceCollector(),
        new ScheduledTaskCollector(),
    ];

    private readonly IReadOnlyList<ICollector> _collectors = collectors ?? DefaultCollectors;

    /// <param name="app">The application.</param>
    /// <param name="session">Users and mounted hives.</param>
    /// <param name="trace">What the application used while it was traced (optional dynamic analysis).</param>
    public CollectionResult Collect(InstalledApp app, CollectionSession session, UsageTrace? trace = null)
    {
        var context = new CollectionContext(app, registry, files, paths, session)
        {
            MsiDatabase = msiDatabase,
            NsisScripts = nsisScripts,
            UsageTrace = trace,
        };
        context.MsiProducts = FindMsiProducts(context);
        context.InstallRoots = InstallRootResolver.Resolve(context);
        if (context.InstallRoots.Count == 0)
            logger.LogWarning("No install directory found for {App}: only registry and data items can be collected", app.DisplayName);

        var machine = session.Machine;
        var knownFolders = machine.All().Select(f => f.Path).Concat(session.Users.SelectMany(u => u.Folders.All().Select(f => f.Path))).ToList();
        context.Keywords = AppKeywords.From(app, context.InstallRoots.Select(r => r.Path),
            folder => knownFolders.Any(k => k.Equals(WinPath.Normalize(folder), StringComparison.OrdinalIgnoreCase)));

        // Dependent collectors use what the main ones found (data folders, COM classes).
        var items = Run(context, CollectionPhase.Main);
        context.MainItems = Merge(items);
        items.AddRange(Run(context, CollectionPhase.Dependent));

        return new CollectionResult(context.InstallRoots, context.Keywords, Merge(items))
        {
            Prerequisites = PrerequisiteAnalyzer.Analyze(files, context.InstallRoots.Select(r => r.Path)),
        };
    }

    /// <summary>The MSI product of the application, or the MSI packages chained by its Burn bundle, largest first.</summary>
    private IReadOnlyList<MsiProduct> FindMsiProducts(CollectionContext context)
    {
        var installer = context.App.Installer;
        var productCodes = new List<string>();
        if (installer?.Properties.GetValueOrDefault(InstallerProperties.ProductCode) is { } productCode)
            productCodes.Add(productCode);
        if (installer?.Type == InstallerType.WixBurn)
            productCodes.AddRange(BurnBundle.FindChainedProducts(context));

        var products = productCodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => MsiRegistry.FindProduct(registry, code))
            .OfType<MsiProductRegistration>()
            .Select(r => new MsiProduct(r, MsiComponents.ForProduct(registry, r.UserDataSid, r.ProductCode)))
            .OrderByDescending(p => p.Components.Count)
            .ToList();
        if (installer?.Type == InstallerType.WixBurn)
            logger.LogDebug("Burn bundle {App}: {Count} chained MSI package(s)", context.App.DisplayName, products.Count);
        return products;
    }

    private List<CollectedItem> Run(CollectionContext context, CollectionPhase phase)
    {
        var items = new List<CollectedItem>();
        foreach (var collector in _collectors.Where(c => c.Phase == phase))
        {
            try
            {
                var found = collector.Collect(context).Where(i => i.Path is null || !context.Session.IsTemporary(i.Path)).ToList();
                logger.LogDebug("{Collector}: {Count} item(s)", collector.Name, found.Count);
                items.AddRange(found);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Collector {Collector} failed on {App}", collector.Name, context.App.Id);
            }
        }
        return items;
    }

    /// <summary>
    /// Removes duplicates (keeping the best confidence) and items already included in a folder/key of at least the same
    /// confidence, then selects Certain and Probable items.
    /// </summary>
    public static IReadOnlyList<CollectedItem> Merge(IEnumerable<CollectedItem> items)
    {
        var unique = items
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.Confidence).First())
            .ToList();

        var containers = unique.Where(i => i.Kind is ItemKind.Directory or ItemKind.RegistryKey).ToList();
        return unique
            .Where(item => !containers.Any(c => !ReferenceEquals(c, item) && c.Confidence >= item.Confidence && Contains(c, item)))
            .Select(item => item with { Selected = item.Confidence >= Confidence.Probable })
            .OrderBy(i => i.Role)
            .ThenBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when <paramref name="item"/> is inside the folder or key <paramref name="container"/>.</summary>
    public static bool Contains(CollectedItem container, CollectedItem item)
    {
        if (container.Kind == ItemKind.Directory && item.Kind is ItemKind.Directory or ItemKind.File)
            return WinPath.IsUnderOrEqual(item.Path!, container.Path!);

        if (container.Kind == ItemKind.RegistryKey && item.Registry is { } location && container.Registry is { } root
            && item.Kind is not ItemKind.PathEntry
            && location.Hive == root.Hive && location.View == root.View
            && string.Equals(location.UserSid, root.UserSid, StringComparison.OrdinalIgnoreCase))
        {
            return WinPath.IsUnderOrEqual(location.Path, root.Path);
        }
        return false;
    }
}
