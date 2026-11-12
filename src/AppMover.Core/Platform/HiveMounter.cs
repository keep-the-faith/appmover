using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AppMover.Core.Models;

namespace AppMover.Core.Platform;

/// <summary>Loads the registry hive (NTUSER.DAT) of a user who is not logged on.</summary>
public interface IHiveMounter
{
    /// <summary>Mounts the hive under HKEY_USERS. Returns null when it cannot be mounted.</summary>
    IHiveMount? Mount(UserProfile profile);
}

public interface IHiveMount : IDisposable
{
    /// <summary>Name of the subkey of HKEY_USERS where the hive (NTUSER.DAT) is mounted.</summary>
    string KeyName { get; }

    /// <summary>
    /// Where the user's classes hive (UsrClass.dat, seen as HKCU\Software\Classes when the user is logged on) is mounted,
    /// or null when it could not be.
    /// </summary>
    string? ClassesKeyName { get; }
}

public sealed class NullHiveMounter : IHiveMounter
{
    public IHiveMount? Mount(UserProfile profile) => null;
}

/// <summary>Uses RegLoadKey/RegUnLoadKey. Requires SeBackupPrivilege and SeRestorePrivilege to be enabled.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHiveMounter : IHiveMounter
{
    public IHiveMount? Mount(UserProfile profile)
    {
        var hiveFile = Path.Combine(profile.ProfilePath, "NTUSER.DAT");
        if (!File.Exists(hiveFile))
            return null;

        var keyName = "AppMover_" + profile.Sid;
        var result = RegLoadKey(HKEY_USERS, keyName, hiveFile);
        if (result != 0)
            throw new Win32Exception(result, $"Cannot load hive {hiveFile}");

        // Per-user classes (file associations, COM) live in a second hive.
        var classesFile = Path.Combine(profile.ProfilePath, @"AppData\Local\Microsoft\Windows\UsrClass.dat");
        var classesKey = keyName + "_Classes";
        var classesLoaded = File.Exists(classesFile) && RegLoadKey(HKEY_USERS, classesKey, classesFile) == 0;
        return new MountedHive(keyName, classesLoaded ? classesKey : null);
    }

    private sealed class MountedHive(string keyName, string? classesKeyName) : IHiveMount
    {
        private bool _disposed;

        public string KeyName => keyName;
        public string? ClassesKeyName => classesKeyName;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            // Handles opened by .NET on the hives must be closed before this point, otherwise unloading fails.
            if (classesKeyName is not null)
                RegUnLoadKey(HKEY_USERS, classesKeyName);
            RegUnLoadKey(HKEY_USERS, keyName);
        }
    }

    private static readonly IntPtr HKEY_USERS = new(unchecked((int)0x80000003));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadKey(IntPtr hKey, string subKey, string file);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegUnLoadKey(IntPtr hKey, string subKey);
}
