using System.Globalization;
using System.Text.RegularExpressions;
using AppMover.Core.Registry;

namespace AppMover.Core.Msi;

public enum MsiKeyPathKind { None, File, Directory, Registry }

/// <summary>
/// Key path of an installed component, as stored by Windows Installer:
/// a file (<c>C:\App\a.exe</c>), a folder (<c>C:\App\Data\</c>) or a registry value (<c>02:\SOFTWARE\Vendor\Value</c>,
/// root code 00 HKCR, 01 HKCU, 02 HKLM, 03 HKU, +20 for the 64-bit view).
/// A value name containing a backslash is stored as <c>NN*offset*\key\value</c>, where offset is the index of the value
/// name in the usual <c>NN:\key\value</c> form (<c>22*59*\SYSTEM\…\VssAccessControl\NT SERVICE\SQLWriter</c>).
/// </summary>
public sealed partial record MsiKeyPath(MsiKeyPathKind Kind, string? Path = null, int RegistryRoot = -1, bool Is64Bit = false, string? ValueName = null)
{
    public static MsiKeyPath Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new MsiKeyPath(MsiKeyPathKind.None);

        if (raw.Length >= 3 && char.IsAsciiDigit(raw[0]) && char.IsAsciiDigit(raw[1]) && raw[2] == ':')
        {
            var path = raw[3..].TrimStart('\\');
            var separator = path.LastIndexOf('\\');
            return Registry(raw, separator < 0 ? path : path[..separator], separator < 0 ? "" : path[(separator + 1)..]);
        }
        if (OffsetRegistryPath().Match(raw) is { Success: true } match)
        {
            // Offset in "NN:\key\value" → index in the part after "NN*offset*", which starts at the same "\key".
            var rest = match.Groups[2].Value;
            var valueStart = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 3;
            if (valueStart < 1 || valueStart > rest.Length || rest[valueStart - 1] != '\\')
                return new MsiKeyPath(MsiKeyPathKind.None);
            return Registry(raw, rest[..(valueStart - 1)].TrimStart('\\'), rest[valueStart..]);
        }
        // Only real paths are files or folders. Others are not on the disk, e.g. "C?\…" (runs from the source media)
        // or "<\Name,version=…" (assembly in the GAC or WinSxS, managed by Windows).
        var isPath = raw.StartsWith(@"\\", StringComparison.Ordinal)
            || (raw.Length >= 3 && char.IsAsciiLetter(raw[0]) && raw[1] == ':' && raw[2] == '\\');
        if (!isPath)
            return new MsiKeyPath(MsiKeyPathKind.None);
        // "C:\?Windows\system32\msvcr120.dll": some key files are stored with a '?' after the root (undocumented;
        // MsiGetComponentPath returns the path without it). The collector checks that the file exists.
        if (raw.Length > 3 && raw[1] == ':' && raw[3] == '?')
            raw = raw[..3] + raw[4..];
        return raw.EndsWith('\\')
            ? new MsiKeyPath(MsiKeyPathKind.Directory, raw.TrimEnd('\\'))
            : new MsiKeyPath(MsiKeyPathKind.File, raw);
    }

    private static MsiKeyPath Registry(string raw, string key, string valueName)
    {
        var code = int.Parse(raw[..2], CultureInfo.InvariantCulture);
        return new MsiKeyPath(MsiKeyPathKind.Registry, key, code % 20, code >= 20, valueName);
    }

    [GeneratedRegex(@"^\d\d\*(\d+)\*(\\.*)$", RegexOptions.Singleline)]
    private static partial Regex OffsetRegistryPath();
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
