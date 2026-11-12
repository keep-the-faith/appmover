using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppMover.Core.Models;
using AppMover.Core.Registry;

namespace AppMover.Packaging;

/// <summary>
/// Content of <c>manifest.json</c>. Paths are tokenized ({ProgramFiles}, {U0.AppData}, {InstallDir}…),
/// see <see cref="Folders"/> for the source value of each token.
/// </summary>
public sealed class PackageManifest
{
    public const int CurrentFormatVersion = 1;
    public const string FileName = "manifest.json";
    public const string RegistryFileName = "registry.json";
    /// <summary>Extension of AppMover packages.</summary>
    public const string Extension = ".appmove";

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string ToolVersion { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required SourceMachine Source { get; init; }
    public required InstalledApp App { get; init; }
    /// <summary>Source path of {InstallDir}.</summary>
    public string? InstallDir { get; init; }
    public required List<PackageUser> Users { get; init; }
    /// <summary>Token → source path.</summary>
    public required Dictionary<string, string> Folders { get; init; }
    /// <summary>Every collected item, with its selection (traceability).</summary>
    public required List<CollectedItem> Items { get; init; }
    public List<PackageFile> Files { get; init; } = [];
    /// <summary>Tokenized folders to create (including empty ones).</summary>
    public List<string> Directories { get; init; } = [];
    /// <summary>Entries to add to list-type environment variables (PATH…).</summary>
    public List<PathEntryRecord> PathEntries { get; init; } = [];
    /// <summary>Shortcuts of the package (recreated when their target moves).</summary>
    public List<ShortcutRecord> Shortcuts { get; init; } = [];
    /// <summary>Scheduled tasks: their XML file is registered, not copied.</summary>
    public List<TaskRecord> Tasks { get; init; } = [];
    /// <summary>Names of the services, created through the service manager.</summary>
    public List<string> Services { get; init; } = [];
    /// <summary>Runtimes to install on the target before running the application.</summary>
    public List<Prerequisite> Prerequisites { get; init; } = [];
    public List<PackageIssue> Issues { get; init; } = [];

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed record SourceMachine(string ComputerName, string OsDescription, string OsVersion, string Architecture);

public sealed record PackageUser(int Index, string Sid, string ProfilePath, string Name);

/// <summary>A file of the package.</summary>
public sealed record PackageFile
{
    /// <summary>Tokenized destination path.</summary>
    public required string Path { get; init; }
    /// <summary>Entry name in the archive.</summary>
    public required string Entry { get; init; }
    public required long Size { get; init; }
    /// <summary>Lower-case hexadecimal SHA-256 of the content.</summary>
    public required string Sha256 { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public required FileAttributes Attributes { get; init; }
    /// <summary>Text file containing machine/user paths: they are adapted to the target when deployed.</summary>
    public bool RewritePaths { get; init; }
    /// <summary>File version of an executable or a DLL ("1.2.3.4"): a newer file already on the target is not replaced.</summary>
    public string? FileVersion { get; init; }
}

/// <summary>A registry key and its values. Partial keys only carry some values: the others must not be removed on deploy.</summary>
public sealed record RegKeyRecord
{
    public required RegHive Hive { get; init; }
    public required RegView View { get; init; }
    /// <summary>For <see cref="RegHive.Users"/>: index of the source user (see <see cref="PackageManifest.Users"/>).</summary>
    public int? UserIndex { get; init; }
    /// <summary>Path relative to the hive (to the user's hive for users).</summary>
    public required string Path { get; init; }
    public bool Partial { get; init; }
    public required List<RegValue> Values { get; init; }
    /// <summary>Names of the values whose data contains path tokens.</summary>
    public List<string>? TokenizedValues { get; init; }
}

public sealed record PathEntryRecord(RegHive Hive, int? UserIndex, string Variable, string Entry);

/// <summary>A .lnk file of the package; every field is tokenized.</summary>
public sealed record ShortcutRecord(string Path, string? Target, string? Arguments, string? WorkingDirectory, string? IconLocation, string? Description)
{
    /// <summary>Windows Installer advertised shortcut (no target: it will not work without the MSI registration).</summary>
    public bool Advertised { get; init; }
}

/// <param name="Name">Task path, e.g. <c>\Vendor\Update</c>.</param>
/// <param name="Path">Tokenized path of its XML file in the package.</param>
public sealed record TaskRecord(string Name, string Path);

public enum IssueSeverity { Warning, Error }

public sealed record PackageIssue(IssueSeverity Severity, string Message, string? Location = null);
