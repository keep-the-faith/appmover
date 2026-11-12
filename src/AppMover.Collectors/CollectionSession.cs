using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Text;
using AppMover.Inventory;
using Microsoft.Extensions.Logging;

namespace AppMover.Collectors;

/// <summary>A user profile taking part in the collection.</summary>
public sealed record SessionUser(int Index, UserProfile Profile, string HiveKey, UserFolders Folders)
{
    /// <summary>Mounted UsrClass.dat of a user who is not logged on (null: Software\Classes of the hive is used).</summary>
    public string? ClassesHiveKey { get; init; }

    public string Sid => Profile.Sid;
    /// <summary>Prefix of this user's path tokens: <c>{U0.AppData}</c>.</summary>
    public string TokenPrefix => $"U{Index}";
}

/// <summary>
/// Machine folders and the user profiles to collect, with their registry hives mounted for the whole
/// duration of the collection and packaging. Dispose to unload the hives.
/// </summary>
public sealed class CollectionSession : IDisposable
{
    private readonly List<IHiveMount> _mounts;

    private CollectionSession(MachineFolders machine, IReadOnlyList<SessionUser> users, List<IHiveMount> mounts)
    {
        Machine = machine;
        Users = users;
        _mounts = mounts;
    }

    public MachineFolders Machine { get; }
    public IReadOnlyList<SessionUser> Users { get; }

    /// <summary>
    /// True for a path in a temporary folder (a user's %TEMP%, Windows\Temp): what they hold never belongs to an
    /// application and is often in use.
    /// </summary>
    public bool IsTemporary(string path) =>
        Users.Select(u => WinPath.Combine(u.Folders.LocalAppData, "Temp"))
            .Append(WinPath.Combine(Machine.Windows, "Temp"))
            .Any(temp => WinPath.IsUnderOrEqual(path, temp));

    public SessionUser? FindUser(string? sid) =>
        sid is null ? null : Users.FirstOrDefault(u => u.Sid.Equals(sid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens a key; per-user locations are resolved in the user's (possibly mounted) hive.</summary>
    public IRegistryKey? OpenKey(IRegistrySource registry, RegistryLocation location) =>
        Resolve(location) is { } physical ? registry.OpenKey(physical.Hive, physical.View, physical.Path) : null;

    /// <summary>
    /// Physical location of a key (HKU\&lt;hive key&gt;\… for users), or null for a user outside the session.
    /// Software\Classes of a user who is not logged on is redirected to their mounted UsrClass.dat.
    /// </summary>
    public (RegHive Hive, RegView View, string Path)? Resolve(RegistryLocation location)
    {
        if (location.Hive != RegHive.Users)
            return (location.Hive, location.View, location.Path);
        if (FindUser(location.UserSid) is not { } user)
            return null;

        const string classes = @"Software\Classes";
        if (user.ClassesHiveKey is { } classesKey && WinPath.IsUnderOrEqual(location.Path, classes))
            return (RegHive.Users, RegView.Default, classesKey + location.Path[classes.Length..]);
        return (RegHive.Users, RegView.Default, $@"{user.HiveKey}\{location.Path}");
    }

    /// <summary>Tokenizer for this machine and these users; <paramref name="installDir"/> becomes {InstallDir}.</summary>
    public PathTokenizer CreateTokenizer(string? installDir)
    {
        var mappings = Machine.All()
            .Concat(Users.SelectMany(u => u.Folders.All().Select(f => ($"{u.TokenPrefix}.{f.Token}", f.Path))));
        if (installDir is not null)
            mappings = mappings.Append((PathTokenizer.InstallDirToken, installDir));
        return new PathTokenizer(mappings);
    }

    /// <summary>Mounts the hives of the requested users (when not logged on) and resolves their folders.</summary>
    /// <param name="profiles">Source of the user profiles.</param>
    /// <param name="mounter">Mounts offline hives.</param>
    /// <param name="folders">Resolves machine and user folders.</param>
    /// <param name="userSids">Profiles to include (unknown SIDs are ignored).</param>
    /// <param name="logger">Logger.</param>
    public static CollectionSession Open(
        UserProfileProvider profiles, IHiveMounter mounter, KnownFoldersResolver folders,
        IEnumerable<string> userSids, ILogger logger)
    {
        var wanted = new HashSet<string>(userSids, StringComparer.OrdinalIgnoreCase);
        var users = new List<SessionUser>();
        var mounts = new List<IHiveMount>();

        foreach (var profile in profiles.GetProfiles().Where(p => wanted.Contains(p.Sid)))
        {
            var hiveKey = profile.Sid;
            string? classesKey = null;
            if (!profile.IsLoaded)
            {
                try
                {
                    var mount = mounter.Mount(profile);
                    if (mount is null)
                    {
                        logger.LogWarning("Hive of {Sid} ({Path}) could not be mounted: its registry data is skipped", profile.Sid, profile.ProfilePath);
                        hiveKey = null;
                    }
                    else
                    {
                        mounts.Add(mount);
                        hiveKey = mount.KeyName;
                        classesKey = mount.ClassesKeyName;
                    }
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Hive of {Sid} ({Path}) could not be mounted: its registry data is skipped", profile.Sid, profile.ProfilePath);
                    hiveKey = null;
                }
            }

            // Without a hive the user's files are still collected; registry lookups simply find nothing.
            users.Add(new SessionUser(users.Count, profile, hiveKey ?? $"(unmounted {profile.Sid})", folders.GetUserFolders(profile, hiveKey))
            {
                ClassesHiveKey = classesKey,
            });
        }

        return new CollectionSession(folders.GetMachineFolders(), users, mounts);
    }

    public void Dispose()
    {
        foreach (var mount in _mounts)
            mount.Dispose();
        _mounts.Clear();
    }
}
