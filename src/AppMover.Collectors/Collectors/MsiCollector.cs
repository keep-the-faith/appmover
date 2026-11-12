using System.Globalization;
using System.Text.RegularExpressions;
using AppMover.Core.Models;
using AppMover.Core.Msi;
using AppMover.Core.Text;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// What Windows Installer knows about an MSI application (or the MSI packages of a Burn bundle): the key paths of its installed components (files and
/// registry values outside the install folders) and the Registry table of its cached .msi database.
/// </summary>
public sealed partial class MsiCollector : ICollector
{
    /// <summary>msidbComponentAttributes64bit.</summary>
    private const int Component64Bit = 0x100;

    public string Name => "Windows Installer";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        return context.MsiProducts.SelectMany(msi => CollectComponents(context, msi).Concat(CollectRegistryTable(context, msi)));
    }

    private IEnumerable<CollectedItem> CollectComponents(CollectionContext context, MsiProduct msi)
    {
        var windows = context.Session.Machine.Windows;
        foreach (var component in msi.Components)
        {
            var keyPath = component.KeyPath;
            switch (keyPath.Kind)
            {
                case MsiKeyPathKind.File or MsiKeyPathKind.Directory:
                {
                    if (context.IsInInstallRoot(keyPath.Path!))
                        continue;
                    var system = WinPath.IsUnderOrEqual(keyPath.Path!, windows);
                    var warning = system ? "System file: it may be shared with other applications or with Windows."
                        : component.Shared ? "Component shared with other installed products." : null;
                    yield return Item(Name, keyPath.Kind == MsiKeyPathKind.File ? ItemKind.File : ItemKind.Directory, ItemRole.Application,
                        warning is null ? Confidence.Probable : Confidence.Possible, "Component of the Windows Installer package",
                        path: keyPath.Path, warning: warning);
                    break;
                }
                case MsiKeyPathKind.Registry:
                    if (context.InstallerRegistryLocation(keyPath.RegistryRoot, keyPath.Path!, keyPath.Is64Bit, msi.PerMachine) is { } location)
                        yield return Item(Name, ItemKind.RegistryValue, ItemRole.Settings, component.Shared ? Confidence.Possible : Confidence.Certain,
                            "Registry key path of a Windows Installer component", registry: location, valueName: keyPath.ValueName,
                            warning: component.Shared ? "Component shared with other installed products." : null);
                    break;
            }
        }
    }

    /// <summary>Rows of the Registry table whose component is installed; keys are resolved with the Property table.</summary>
    private IEnumerable<CollectedItem> CollectRegistryTable(CollectionContext context, MsiProduct msi)
    {
        var database = msi.Registration.LocalPackage;
        if (context.MsiDatabase is not { } reader || database is null || !context.Files.FileExists(database))
            yield break;

        var installed = msi.Components.Select(c => c.PackedId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var components = (reader.Query(database, "SELECT `Component`, `ComponentId`, `Attributes` FROM `Component`") ?? [])
            .Where(r => r[0] is not null && r[1] is not null)
            .ToDictionary(r => r[0]!, r => (Packed: MsiGuid.Pack(r[1]!), Attributes: int.TryParse(r[2], CultureInfo.InvariantCulture, out var a) ? a : 0));
        var properties = (reader.Query(database, "SELECT `Property`, `Value` FROM `Property`") ?? [])
            .Where(r => r[0] is not null)
            .ToDictionary(r => r[0]!, r => r[1] ?? "");
        var rows = reader.Query(database, "SELECT `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`") ?? [];

        foreach (var row in rows)
        {
            if (row[4] is null || !components.TryGetValue(row[4]!, out var component) || component.Packed is null
                || !installed.Contains(component.Packed) || !int.TryParse(row[0], CultureInfo.InvariantCulture, out var root)
                || Resolve(row[1], properties) is not { } key)
                continue;

            var name = row[2];
            if (name == "+") // only ensures that the key exists
                continue;
            if (context.InstallerRegistryLocation(root, key, (component.Attributes & Component64Bit) != 0, msi.PerMachine) is not { } location)
                continue;

            // "-" and "*" with no value: the key is removed with the component, so it belongs to the application.
            if (name is "-" or "*" && row[3] is null)
                yield return Item(Name, ItemKind.RegistryKey, ItemRole.Settings, Confidence.Certain,
                    "Registry key owned by the Windows Installer package", registry: location);
            else
                yield return Item(Name, ItemKind.RegistryValue, ItemRole.Settings, Confidence.Certain,
                    "Registry value of the Windows Installer package", registry: location, valueName: name ?? "");
        }
    }

    /// <summary>Replaces [Property] references; null when one cannot be resolved (formatted keys like [~] or [#file]).</summary>
    private static string? Resolve(string? key, IReadOnlyDictionary<string, string> properties)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        var unresolved = false;
        var result = PropertyReference().Replace(key, m =>
        {
            if (properties.TryGetValue(m.Groups[1].Value, out var value))
                return value;
            unresolved = true;
            return m.Value;
        });
        return unresolved || result.Contains('[') ? null : result;
    }

    [GeneratedRegex(@"\[([A-Za-z_][A-Za-z0-9_.]*)\]")]
    private static partial Regex PropertyReference();
}
