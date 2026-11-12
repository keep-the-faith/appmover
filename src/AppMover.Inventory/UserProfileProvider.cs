using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Registry;

namespace AppMover.Inventory;

/// <summary>Lists real user profiles (S-1-5-21-*) from HKLM\...\ProfileList.</summary>
public sealed class UserProfileProvider(IRegistrySource registry, IPathResolver paths)
{
    public const string ProfileListPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public IReadOnlyList<UserProfile> GetProfiles()
    {
        using var list = registry.OpenKey(RegHive.LocalMachine, RegView.Registry64, ProfileListPath);
        if (list is null)
            return [];

        HashSet<string> loaded;
        using (var users = registry.OpenKey(RegHive.Users, RegView.Default, ""))
            loaded = new HashSet<string>(users?.GetSubKeyNames() ?? [], StringComparer.OrdinalIgnoreCase);

        var profiles = new List<UserProfile>();
        foreach (var sid in list.GetSubKeyNames())
        {
            // Skip system accounts (S-1-5-18/19/20) and backup entries left by a corrupted profile (*.bak).
            if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                continue;

            using var key = list.OpenSubKey(sid);
            var path = key?.GetString("ProfileImagePath");
            if (string.IsNullOrWhiteSpace(path))
                continue;

            profiles.Add(new UserProfile(sid, paths.Expand(path), loaded.Contains(sid)));
        }
        return profiles;
    }
}
