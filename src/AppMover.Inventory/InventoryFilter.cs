using AppMover.Core.Models;

namespace AppMover.Inventory;

/// <summary>Reproduces the rules used by "Apps &amp; features" to hide entries.</summary>
public static class InventoryFilter
{
    private static readonly string[] UpdateReleaseTypes = ["Update", "Hotfix", "Security Update", "Service Pack", "Update Rollup"];

    /// <returns>The reason why the entry is hidden, or null if it is visible.</returns>
    public static string? GetHiddenReason(InstalledApp app)
    {
        if (string.IsNullOrWhiteSpace(app.DisplayName))
            return "No DisplayName";
        if (app.SystemComponent)
            return "SystemComponent=1";
        if (app.RawValues.ContainsKey("ParentKeyName"))
            return "Child entry (ParentKeyName)";
        if (app.RawValues.TryGetValue("ReleaseType", out var type)
            && UpdateReleaseTypes.Contains(type.AsString(), StringComparer.OrdinalIgnoreCase))
            return $"ReleaseType={type.AsString()}";
        if (string.IsNullOrWhiteSpace(app.UninstallString) && !app.WindowsInstaller)
            return "No UninstallString";
        return null;
    }
}
