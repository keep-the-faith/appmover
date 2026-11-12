using System.Globalization;
using AppMover.Core.Registry;

namespace AppMover.Core.Msi;

public enum MsiKeyPathKind { None, File, Directory, Registry }

/// <summary>
/// Key path of an installed component, as stored by Windows Installer:
/// a file (<c>C:\App\a.exe</c>), a folder (<c>C:\App\Data\</c>) or a registry value (<c>02:\SOFTWARE\Vendor\Value</c>,
/// root code 00 HKCR, 01 HKCU, 02 HKLM, 03 HKU, +20 for the 64-bit view).
/// </summary>
public sealed record MsiKeyPath(MsiKeyPathKind Kind, string? Path = null, int RegistryRoot = -1, bool Is64Bit = false, string? ValueName = null)
{
    public static MsiKeyPath Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new MsiKeyPath(MsiKeyPathKind.None);

        if (raw.Length >= 3 && char.IsAsciiDigit(raw[0]) && char.IsAsciiDigit(raw[1]) && raw[2] == ':')
        {
            var code = int.Parse(raw[..2], CultureInfo.InvariantCulture);
            var path = raw[3..].TrimStart('\\');
            var separator = path.LastIndexOf('\\');
            return new MsiKeyPath(MsiKeyPathKind.Registry,
                separator < 0 ? path : path[..separator], code % 20, code >= 20,
                separator < 0 ? "" : path[(separator + 1)..]);
        }
        // "C?\…": the component runs from the source media, it is not on the disk.
        if (raw.Length >= 2 && raw[1] == '?')
            return new MsiKeyPath(MsiKeyPathKind.None);
        return raw.EndsWith('\\')
            ? new MsiKeyPath(MsiKeyPathKind.Directory, raw.TrimEnd('\\'))
            : new MsiKeyPath(MsiKeyPathKind.File, raw);
    }
}

/// <summary>An installed component of a product.</summary>
/// <param name="PackedId">Packed component GUID (registry key name).</param>
/// <param name="KeyPath">Where the component is.</param>
/// <param name="Shared">Also installed by another product.</param>
public sealed record MsiComponent(string PackedId, MsiKeyPath KeyPath, bool Shared);

public static class MsiComponents
{
    /// <summary>Value name used by Windows Installer for permanent components (not a product).</summary>
    private const string PermanentProduct = "00000000000000000000000000000000";

    /// <summary>
    /// Components of a product, from <c>Installer\UserData\&lt;SID&gt;\Components\&lt;component&gt;\&lt;product&gt; = key path</c>.
    /// </summary>
    public static IReadOnlyList<MsiComponent> ForProduct(IRegistrySource registry, string userDataSid, string productCode)
    {
        var product = MsiGuid.Pack(productCode);
        if (product is null)
            return [];

        using var components = registry.OpenKey(RegHive.LocalMachine, RegView.Registry64, $@"{MsiRegistry.UserDataPath}\{userDataSid}\Components");
        if (components is null)
            return [];

        var result = new List<MsiComponent>();
        foreach (var packedId in components.GetSubKeyNames())
        {
            using var component = components.OpenSubKey(packedId);
            if (component?.GetValue(product) is not { } keyPath)
                continue;
            var shared = component.GetValueNames().Any(n =>
                !n.Equals(product, StringComparison.OrdinalIgnoreCase) && !n.Equals(PermanentProduct, StringComparison.Ordinal));
            result.Add(new MsiComponent(packedId, MsiKeyPath.Parse(keyPath.AsString()), shared));
        }
        return result;
    }
}
