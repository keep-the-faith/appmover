using AppMover.Core.Models;
using AppMover.Core.Msi;
using AppMover.Core.Registry;

namespace AppMover.Collectors;

/// <summary>
/// A WiX Burn bundle installs nothing itself: its files belong to the MSI packages it chains. Burn records itself as a
/// dependent of each package in <c>Software\Classes\Installer\Dependencies\&lt;package provider&gt;\Dependents\&lt;bundle provider&gt;</c>;
/// the provider key is named after the product code, or holds it as its default value.
/// </summary>
public static class BurnBundle
{
    private const string DependenciesPath = @"Software\Classes\Installer\Dependencies";

    /// <summary>Product codes of the MSI packages installed by the bundle.</summary>
    public static IReadOnlyList<string> FindChainedProducts(CollectionContext context)
    {
        var app = context.App;
        // The bundle registers its dependents under its provider key (the bundle id unless the author set one).
        var bundleKeys = new[] { app.RawValues.GetValueOrDefault("BundleProviderKey")?.AsString(), app.KeyName }
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scopes = new List<RegistryLocation> { RegistryLocation.Machine(RegView.Registry64, DependenciesPath) };
        if (app.Scope == AppScope.User && app.UserSid is not null)
            scopes.Add(RegistryLocation.User(app.UserSid, DependenciesPath));

        var products = new List<string>();
        foreach (var scope in scopes)
        {
            using var dependencies = context.Open(scope);
            if (dependencies is null)
                continue;
            foreach (var provider in dependencies.GetSubKeyNames())
            {
                using var key = dependencies.OpenSubKey(provider);
                using var dependents = key?.OpenSubKey("Dependents");
                if (dependents is null || !dependents.GetSubKeyNames().Any(bundleKeys.Contains))
                    continue;
                var productCode = key!.GetString("") is { } value && MsiGuid.IsBracedGuid(value) ? value
                    : MsiGuid.IsBracedGuid(provider) ? provider : null;
                if (productCode is not null)
                    products.Add(productCode);
            }
        }
        return products.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
