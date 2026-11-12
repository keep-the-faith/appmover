using AppMover.Core.Models;
using AppMover.Core.Registry;
using AppMover.Core.Text;

namespace AppMover.Core.Platform;

/// <summary>Machine-wide folders.</summary>
public sealed record MachineFolders
{
    public required string Windows { get; init; }
    public required string System32 { get; init; }
    public required string SysWow64 { get; init; }
    public required string ProgramFiles { get; init; }
    public required string ProgramFilesX86 { get; init; }
    public required string ProgramData { get; init; }
    public required string Public { get; init; }
    public required string CommonDesktop { get; init; }
    public required string CommonStartMenu { get; init; }

    /// <summary>Token name → path.</summary>
    public IEnumerable<(string Token, string Path)> All() =>
    [
        ("Windows", Windows), ("System32", System32), ("SysWOW64", SysWow64),
        ("ProgramFiles", ProgramFiles), ("ProgramFilesX86", ProgramFilesX86), ("ProgramData", ProgramData),
        ("Public", Public), ("CommonDesktop", CommonDesktop), ("CommonStartMenu", CommonStartMenu),
    ];
}

/// <summary>Folders of one user profile (honours folder redirection declared in "User Shell Folders").</summary>
public sealed record UserFolders
{
    public required string Profile { get; init; }
    public required string AppData { get; init; }
    public required string LocalAppData { get; init; }
    public required string LocalAppDataLow { get; init; }
    public required string Documents { get; init; }
    public required string Desktop { get; init; }
    public required string StartMenu { get; init; }

    /// <summary>Token suffix → path.</summary>
    public IEnumerable<(string Token, string Path)> All() =>
    [
        ("Profile", Profile), ("AppData", AppData), ("LocalAppData", LocalAppData), ("LocalAppDataLow", LocalAppDataLow),
        ("Documents", Documents), ("Desktop", Desktop), ("StartMenu", StartMenu),
    ];
}

/// <summary>Resolves known folders from the registry, so it also works for users who are not logged on.</summary>
public sealed class KnownFoldersResolver(IRegistrySource registry, IPathResolver paths)
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private const string ShellFolders = $@"{CurrentVersion}\Explorer\Shell Folders";
    private const string UserShellFolders = $@"{CurrentVersion}\Explorer\User Shell Folders";
    private const string LocalLowId = "{A520A1A4-1780-4FF6-BD18-167343C5AF16}";

    public MachineFolders GetMachineFolders()
    {
        string Read(string path, string name, string fallback)
        {
            using var key = registry.OpenKey(RegHive.LocalMachine, RegView.Registry64, path);
            var value = key?.GetString(name);
            return WinPath.Normalize(paths.Expand(string.IsNullOrWhiteSpace(value) ? fallback : value));
        }

        var windows = Read(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "SystemRoot", @"C:\Windows");
        var programData = Read(ShellFolders, "Common AppData", @"C:\ProgramData");
        return new MachineFolders
        {
            Windows = windows,
            System32 = WinPath.Combine(windows, "System32"),
            SysWow64 = WinPath.Combine(windows, "SysWOW64"),
            ProgramFiles = Read(CurrentVersion, "ProgramFilesDir", @"C:\Program Files"),
            ProgramFilesX86 = Read(CurrentVersion, "ProgramFilesDir (x86)", @"C:\Program Files (x86)"),
            ProgramData = programData,
            Public = Read(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", "Public", @"C:\Users\Public"),
            CommonDesktop = Read(ShellFolders, "Common Desktop", @"C:\Users\Public\Desktop"),
            CommonStartMenu = Read(ShellFolders, "Common Start Menu", WinPath.Combine(programData, @"Microsoft\Windows\Start Menu")),
        };
    }

    /// <param name="profile">The user profile.</param>
    /// <param name="hiveKey">Name of the user's hive under HKEY_USERS (SID, or mount name for an offline hive).</param>
    public UserFolders GetUserFolders(UserProfile profile, string? hiveKey)
    {
        var root = WinPath.Normalize(profile.ProfilePath);
        using var key = hiveKey is null ? null : registry.OpenKey(RegHive.Users, RegView.Default, $@"{hiveKey}\{UserShellFolders}");

        string Read(string name, string relativeDefault)
        {
            var value = key?.GetString(name);
            if (string.IsNullOrWhiteSpace(value))
                return WinPath.Combine(root, relativeDefault);
            // %USERPROFILE% must be this user's profile, not the one of the user running AppMover.
            value = value.Replace("%USERPROFILE%", root, StringComparison.OrdinalIgnoreCase);
            return WinPath.Normalize(paths.Expand(value));
        }

        return new UserFolders
        {
            Profile = root,
            AppData = Read("AppData", @"AppData\Roaming"),
            LocalAppData = Read("Local AppData", @"AppData\Local"),
            LocalAppDataLow = Read(LocalLowId, @"AppData\LocalLow"),
            Documents = Read("Personal", "Documents"),
            Desktop = Read("Desktop", "Desktop"),
            StartMenu = Read("Start Menu", @"AppData\Roaming\Microsoft\Windows\Start Menu"),
        };
    }
}
