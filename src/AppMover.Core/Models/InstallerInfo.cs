namespace AppMover.Core.Models;

public enum InstallerType
{
    Unknown,
    Msi,
    WixBurn,
    InnoSetup,
    Nsis,
    InstallShield,
    Squirrel,
    ClickOnce,
}

/// <summary>One clue supporting a detection, with its weight (0-100).</summary>
public sealed record Evidence(string Description, int Weight);

public sealed record SecondaryMatch(InstallerType Type, int Confidence);

/// <summary>Result of the installer detection for one application.</summary>
public sealed class InstallerInfo
{
    public required InstallerType Type { get; init; }
    /// <summary>0-100.</summary>
    public required int Confidence { get; init; }
    public required IReadOnlyList<Evidence> Evidence { get; init; }
    /// <summary>Other technologies that also matched (e.g. InstallShield wrapping an MSI).</summary>
    public IReadOnlyList<SecondaryMatch> Secondary { get; init; } = [];
    /// <summary>Expanded path of the uninstaller executable, if any.</summary>
    public string? UninstallerPath { get; init; }
    /// <summary>Installer-specific data, see <see cref="InstallerProperties"/>.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether AppMover can transfer this kind of installation.</summary>
    public bool IsTransferable => Type is not InstallerType.ClickOnce;
}

/// <summary>Well-known keys of <see cref="InstallerInfo.Properties"/>.</summary>
public static class InstallerProperties
{
    /// <summary>MSI product code ({GUID}).</summary>
    public const string ProductCode = "ProductCode";
    /// <summary>Cached MSI database in C:\Windows\Installer.</summary>
    public const string LocalPackage = "LocalPackage";
    /// <summary>Installer\UserData SID under which the MSI product is registered (S-1-5-18 = per-machine).</summary>
    public const string MsiUserDataSid = "MsiUserDataSid";
    /// <summary>Inno Setup uninstall log (unins000.dat).</summary>
    public const string UninstallLog = "UninstallLog";
    /// <summary>WiX Burn cached bundle executable.</summary>
    public const string BundleCachePath = "BundleCachePath";
    /// <summary>Install directory declared by the installer itself (e.g. "Inno Setup: App Path").</summary>
    public const string InstallerAppPath = "InstallerAppPath";
}
