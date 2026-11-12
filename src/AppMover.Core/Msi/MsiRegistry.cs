using AppMover.Core.Registry;

namespace AppMover.Core.Msi;

/// <summary>Registration of an MSI product in HKLM\...\Installer\UserData.</summary>
public sealed record MsiProductRegistration(string ProductCode, string UserDataSid, string? LocalPackage, string? InstallLocation);

/// <summary>Reads Windows Installer data directly from the registry (no msi.dll needed, works on archived/offline registries).</summary>
public static class MsiRegistry
{
    public const string UserDataPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData";

    /// <summary>Finds where a product is registered. Per-machine installs are under S-1-5-18.</summary>
    public static MsiProductRegistration? FindProduct(IRegistrySource registry, string productCode)
    {
        var packed = MsiGuid.Pack(productCode);
        if (packed is null)
            return null;

        using var userData = registry.OpenKey(RegHive.LocalMachine, RegView.Registry64, UserDataPath);
        if (userData is null)
            return null;

        foreach (var sid in userData.GetSubKeyNames())
        {
            using var props = userData.OpenSubKey($@"{sid}\Products\{packed}\InstallProperties");
            if (props is null)
                continue;
            return new MsiProductRegistration(
                productCode,
                sid,
                props.GetString("LocalPackage"),
                props.GetString("InstallLocation"));
        }
        return null;
    }
}
