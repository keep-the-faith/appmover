using AppMover.Core.Registry;

namespace AppMover.Core.Models;

public enum AppScope { Machine, User }

/// <summary>A local user profile (from HKLM\...\ProfileList).</summary>
public sealed record UserProfile(string Sid, string ProfilePath, bool IsLoaded);

/// <summary>An application registered in an "Uninstall" registry key.</summary>
public sealed class InstalledApp
{
    /// <summary>Unique id: <c>&lt;source&gt;\&lt;key name&gt;</c>, e.g. <c>HKLM64\{GUID}</c> or <c>HKU\S-1-5-21-…\App</c>.</summary>
    public required string Id { get; init; }
    /// <summary>Name of the subkey under Uninstall.</summary>
    public required string KeyName { get; init; }
    /// <summary>Full registry path of the Uninstall entry, as seen when it was read.</summary>
    public required string RegistryPath { get; init; }
    public required AppScope Scope { get; init; }
    /// <summary>Owner SID for per-user installations.</summary>
    public string? UserSid { get; init; }
    public required RegView View { get; init; }

    public string? DisplayName { get; init; }
    public string? DisplayVersion { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    /// <summary>Raw value, usually yyyyMMdd.</summary>
    public string? InstallDate { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }
    public long? EstimatedSizeKb { get; init; }
    public bool WindowsInstaller { get; init; }
    public bool SystemComponent { get; init; }

    /// <summary>Why "Apps &amp; features" would hide this entry, or null if visible.</summary>
    public string? HiddenReason { get; set; }

    /// <summary>All values of the Uninstall key (kept for the archive).</summary>
    public required IReadOnlyDictionary<string, RegValue> RawValues { get; init; }

    /// <summary>Set by the detection step.</summary>
    public InstallerInfo? Installer { get; set; }

    public override string ToString() => $"{DisplayName ?? KeyName} [{Id}]";
}
