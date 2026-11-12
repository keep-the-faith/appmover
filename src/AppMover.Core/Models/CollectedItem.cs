using System.Text.Json.Serialization;
using AppMover.Core.Registry;

namespace AppMover.Core.Models;

public enum ItemKind
{
    /// <summary>A folder and all its content.</summary>
    Directory,
    File,
    /// <summary>A registry key and all its subkeys.</summary>
    RegistryKey,
    /// <summary>One value of a registry key (the other values of the key are not touched).</summary>
    RegistryValue,
    /// <summary>One entry of a list-type environment variable such as PATH.</summary>
    PathEntry,
}

/// <summary>What the item is for (drives how it is deployed).</summary>
public enum ItemRole
{
    Application,
    Data,
    Settings,
    UninstallEntry,
    Shortcut,
    Service,
    Driver,
    ScheduledTask,
    Startup,
    Com,
    FileAssociation,
    AppPath,
    Environment,
    FirewallRule,
}

public enum Confidence { Possible = 1, Probable = 2, Certain = 3 }

/// <summary>Location of a registry key. For <see cref="RegHive.Users"/>, <see cref="Path"/> is relative to the user's hive.</summary>
public sealed record RegistryLocation(RegHive Hive, RegView View, string Path, string? UserSid = null)
{
    public static RegistryLocation Machine(RegView view, string path) => new(RegHive.LocalMachine, view, path);
    public static RegistryLocation User(string sid, string path) => new(RegHive.Users, RegView.Default, path, sid);

    public override string ToString() => Hive switch
    {
        RegHive.LocalMachine => (View == RegView.Registry32 ? @"HKLM32\" : @"HKLM64\") + Path,
        RegHive.Users => $@"HKU\{UserSid}\{Path}",
        _ => $@"{Hive}\{Path}",
    };
}

/// <summary>A file, folder, registry key/value… belonging to the application, found by a collector.</summary>
public sealed record CollectedItem
{
    public required ItemKind Kind { get; init; }
    public required ItemRole Role { get; init; }
    public required Confidence Confidence { get; init; }
    /// <summary>Name of the collector that found it.</summary>
    public required string Collector { get; init; }
    /// <summary>Why it is attributed to the application.</summary>
    public required string Reason { get; init; }

    /// <summary>File system path (Directory, File).</summary>
    public string? Path { get; init; }
    /// <summary>Registry location (RegistryKey, RegistryValue).</summary>
    public RegistryLocation? Registry { get; init; }
    /// <summary>Value name (RegistryValue), or environment variable name (PathEntry).</summary>
    public string? ValueName { get; init; }
    /// <summary>Entry of the environment variable (PathEntry).</summary>
    public string? Entry { get; init; }
    /// <summary>Owner profile of a per-user item.</summary>
    public string? UserSid { get; init; }

    /// <summary>Included in the package. Default: Certain and Probable items.</summary>
    public bool Selected { get; set; }

    /// <summary>Something the user should know (e.g. a driver that will not be deployed automatically).</summary>
    public string? Warning { get; init; }
    /// <summary>Extra data (shortcut target, task name…).</summary>
    public Dictionary<string, string>? Details { get; init; }

    /// <summary>Stable identity, used for de-duplication and for the selection file.</summary>
    [JsonIgnore]
    public string Id => Kind switch
    {
        ItemKind.Directory or ItemKind.File => $"fs:{Path}",
        ItemKind.RegistryKey => $"reg:{Registry}",
        ItemKind.RegistryValue => $"regval:{Registry}|{ValueName}",
        ItemKind.PathEntry => $"env:{Registry}|{ValueName}|{Entry}",
        _ => throw new InvalidOperationException($"Unknown kind {Kind}"),
    };

    /// <summary>Human readable location.</summary>
    [JsonIgnore]
    public string Location => Kind switch
    {
        ItemKind.Directory => Path + @"\",
        ItemKind.File => Path ?? "",
        ItemKind.RegistryKey => Registry?.ToString() ?? "",
        ItemKind.RegistryValue => $"{Registry} → {(string.IsNullOrEmpty(ValueName) ? "(Default)" : ValueName)}",
        ItemKind.PathEntry => $"{Registry} → {ValueName} += {Entry}",
        _ => "",
    };
}
