using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using Microsoft.Extensions.Logging;

namespace AppMover.Inventory;

public sealed record InventoryOptions
{
    /// <summary>Also read the per-user installations of the other users (mounts offline hives).</summary>
    public bool AllUsers { get; init; }
    /// <summary>Also return the entries hidden by "Apps &amp; features" (updates, system components…).</summary>
    public bool IncludeHidden { get; init; }
}

/// <summary>Step 1: lists the applications registered in the Uninstall keys.</summary>
public sealed class InventoryService(
    IRegistrySource registry,
    UserProfileProvider profiles,
    IHiveMounter hiveMounter,
    string? currentUserSid,
    ILogger<InventoryService> logger)
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledApp> GetInstalledApps(InventoryOptions options)
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        ReadUninstallKey(apps, RegHive.LocalMachine, RegView.Registry64, UninstallPath, "HKLM64", AppScope.Machine, null);
        ReadUninstallKey(apps, RegHive.LocalMachine, RegView.Registry32, UninstallPath, "HKLM32", AppScope.Machine, null);

        var currentUserRead = false;
        foreach (var profile in profiles.GetProfiles())
        {
            var isCurrent = string.Equals(profile.Sid, currentUserSid, StringComparison.OrdinalIgnoreCase);
            if (!options.AllUsers && !isCurrent)
                continue;

            var tag = $@"HKU\{profile.Sid}";
            if (profile.IsLoaded)
            {
                ReadUninstallKey(apps, RegHive.Users, RegView.Default, $@"{profile.Sid}\{UninstallPath}", tag, AppScope.User, profile.Sid);
                currentUserRead |= isCurrent;
                continue;
            }

            try
            {
                using var mount = hiveMounter.Mount(profile);
                if (mount is null)
                {
                    logger.LogWarning("Hive of {Sid} ({Path}) could not be mounted", profile.Sid, profile.ProfilePath);
                    continue;
                }
                ReadUninstallKey(apps, RegHive.Users, RegView.Default, $@"{mount.KeyName}\{UninstallPath}", tag, AppScope.User, profile.Sid);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Hive of {Sid} ({Path}) could not be mounted", profile.Sid, profile.ProfilePath);
            }
        }

        // Fallback when the current user does not appear in ProfileList (e.g. unknown SID).
        if (!currentUserRead)
            ReadUninstallKey(apps, RegHive.CurrentUser, RegView.Default, UninstallPath, "HKCU", AppScope.User, currentUserSid);

        foreach (var app in apps.Values)
            app.HiddenReason = InventoryFilter.GetHiddenReason(app);

        return apps.Values
            .Where(a => options.IncludeHidden || a.HiddenReason is null)
            .OrderBy(a => a.DisplayName ?? a.KeyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ReadUninstallKey(Dictionary<string, InstalledApp> apps, RegHive hive, RegView view, string path,
        string tag, AppScope scope, string? sid)
    {
        using var uninstall = registry.OpenKey(hive, view, path);
        if (uninstall is null)
        {
            logger.LogDebug("Uninstall key not found: {Hive} {View} {Path}", hive, view, path);
            return;
        }

        foreach (var keyName in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(keyName);
            if (key is null)
            {
                logger.LogWarning("Cannot open {Path}\\{Key}", uninstall.Path, keyName);
                continue;
            }

            var app = CreateApp(key, keyName, $@"{tag}\{keyName}", scope, sid, view);
            apps.TryAdd(app.Id, app);
        }
    }

    private static InstalledApp CreateApp(IRegistryKey key, string keyName, string id, AppScope scope, string? sid, RegView view)
    {
        var values = key.GetAllValues();
        string? Str(string name) => values.TryGetValue(name, out var v) ? NullIfBlank(v.AsString()) : null;
        long? Num(string name) => values.TryGetValue(name, out var v) ? v.AsNumber() : null;

        return new InstalledApp
        {
            Id = id,
            KeyName = keyName,
            RegistryPath = key.Path,
            Scope = scope,
            UserSid = sid,
            View = view,
            DisplayName = Str("DisplayName")?.Trim(),
            DisplayVersion = Str("DisplayVersion"),
            Publisher = Str("Publisher")?.Trim(),
            InstallLocation = Str("InstallLocation"),
            InstallDate = Str("InstallDate"),
            UninstallString = Str("UninstallString"),
            QuietUninstallString = Str("QuietUninstallString"),
            DisplayIcon = Str("DisplayIcon"),
            EstimatedSizeKb = Num("EstimatedSize"),
            WindowsInstaller = Num("WindowsInstaller") == 1,
            SystemComponent = Num("SystemComponent") == 1,
            RawValues = values,
        };
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
