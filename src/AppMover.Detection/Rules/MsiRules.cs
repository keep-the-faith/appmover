using System.Text.RegularExpressions;
using AppMover.Core.Models;
using AppMover.Core.Msi;

namespace AppMover.Detection.Rules;

/// <summary>Windows Installer package (.msi).</summary>
public sealed partial class MsiRule : IDetectionRule
{
    public InstallerType Type => InstallerType.Msi;

    public DetectionCandidate? Evaluate(InstalledApp app, DetectionContext context)
    {
        var candidate = new DetectionCandidate(Type);

        if (app.WindowsInstaller)
            candidate.Add("WindowsInstaller=1", 50);

        string? productCode = null;
        if (MsiGuid.IsBracedGuid(app.KeyName))
        {
            productCode = app.KeyName;
            candidate.Add("Uninstall key name is a product code (GUID)", 10);
        }

        var msiexecLine = DetectionContext.GetCommandLines(app)
            .FirstOrDefault(c => c.Contains("msiexec", StringComparison.OrdinalIgnoreCase));
        if (msiexecLine is not null)
        {
            candidate.Add("Uninstall command uses msiexec", 30);
            productCode ??= GuidPattern().Match(msiexecLine) is { Success: true } m ? m.Value.ToUpperInvariant() : null;
        }

        if (productCode is not null)
        {
            candidate.Set(InstallerProperties.ProductCode, productCode);
            if (MsiRegistry.FindProduct(context.Registry, productCode) is { } registration)
            {
                candidate.Add($"Product registered in Installer\\UserData\\{registration.UserDataSid}", 30)
                    .Set(InstallerProperties.MsiUserDataSid, registration.UserDataSid)
                    .Set(InstallerProperties.LocalPackage, registration.LocalPackage);
                if (registration.LocalPackage is not null && context.Files.FileExists(registration.LocalPackage))
                    candidate.Add("Cached MSI database found", 10);
            }
        }

        // A GUID key name alone (10) stays below the detector threshold: other installers use GUID key names too.
        return candidate.NullIfEmpty();
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidPattern();
}

/// <summary>WiX Burn bundle (setup.exe chaining one or more MSI/EXE packages).</summary>
public sealed class WixBurnRule : IDetectionRule
{
    public InstallerType Type => InstallerType.WixBurn;

    public DetectionCandidate? Evaluate(InstalledApp app, DetectionContext context)
    {
        var candidate = new DetectionCandidate(Type);
        if (app.RawValues.TryGetValue("BundleCachePath", out var cachePath))
        {
            candidate.Add("BundleCachePath present", 60)
                .Set(InstallerProperties.BundleCachePath, cachePath.AsString() is { } p ? context.Paths.Expand(p) : null);
        }
        if (app.RawValues.ContainsKey("BundleProviderKey"))
            candidate.Add("BundleProviderKey present", 20);
        if (app.RawValues.ContainsKey("BundleUpgradeCode"))
            candidate.Add("BundleUpgradeCode present", 20);
        return candidate.NullIfEmpty();
    }
}
