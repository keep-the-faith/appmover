using AppMover.Core.Models;
using AppMover.Core.Registry;
using AppMover.Core.Text;
using AppMover.Packaging;

namespace AppMover.Deployment;

public sealed record DeployOptions
{
    /// <summary>Install directory on the target; default: the source one, relocated (e.g. other Program Files).</summary>
    public string? InstallDir { get; init; }
    /// <summary>Source user index → target user SID. Unmapped users are skipped.</summary>
    public required IReadOnlyDictionary<int, string> UserMap { get; init; }
    /// <summary>Deploy even if the application is already installed / replace existing tasks.</summary>
    public bool Force { get; init; }
    /// <summary>Command registered as UninstallString, followed by the deployment id.</summary>
    public required string UninstallCommand { get; init; }
}

/// <summary>Everything that will be done, computed without changing the machine (dry run).</summary>
public sealed class DeploymentPlan
{
    public required string Id { get; init; }
    public required PackageManifest Manifest { get; init; }
    public string? InstallDir { get; init; }
    /// <summary>Token → folder on the target machine.</summary>
    public required TokenMap Target { get; init; }
    /// <summary>Target users taking part in the deployment.</summary>
    public required IReadOnlyList<string> UserSids { get; init; }
    public required IReadOnlyList<DeployOperation> Operations { get; init; }
    public required IReadOnlyList<PackageIssue> Issues { get; init; }
    /// <summary>Converts the source paths found in a text (configuration file, task) into target paths.</summary>
    public required Func<string, string> RelocateText { get; init; }
    public long BytesToWrite => Operations.OfType<ExtractFileOperation>().Sum(o => o.File.Size);
    public bool CanProceed => Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>One step of a deployment.</summary>
public abstract record DeployOperation
{
    public abstract string Describe();
}

public sealed record CreateDirectoryOperation(string Path) : DeployOperation
{
    public override string Describe() => $"Create folder   {Path}";
}

public sealed record ExtractFileOperation(PackageFile File, string Target) : DeployOperation
{
    public override string Describe() => $"Copy file       {Target}";
}

public sealed record CreateShortcutOperation(string Path, ShortcutTarget Shortcut) : DeployOperation
{
    public override string Describe() => $"Shortcut        {Path} → {Shortcut.Target}";
}

public sealed record CreateServiceOperation(ServiceSpec Spec) : DeployOperation
{
    public override string Describe() => $"Create service  {Spec.Name} ({Spec.ImagePath})";
}

public sealed record WriteRegistryOperation(RegistryLocation Location, IReadOnlyList<RegValue> Values, bool Partial) : DeployOperation
{
    public override string Describe() => $"Registry        {Location}{(Partial ? $" [{string.Join(", ", Values.Select(v => v.Name))}]" : "")}";
}

public sealed record AddPathEntryOperation(RegistryLocation Location, string Variable, string Entry) : DeployOperation
{
    public override string Describe() => $"Environment     %{Variable}% += {Entry}";
}

public sealed record RegisterTaskOperation(string Name, string Xml) : DeployOperation
{
    public override string Describe() => $"Scheduled task  {Name}";
}

public sealed record WriteUninstallEntryOperation(RegistryLocation Location, string Command) : DeployOperation
{
    public override string Describe() => $"Uninstall entry {Location} → {Command}";
}

public enum UndoKind
{
    DirectoryCreated,
    FileCreated,
    FileReplaced,
    RegistryKeyCreated,
    RegistryValueSet,
    ServiceCreated,
    TaskRegistered,
    PathEntryAdded,
}

/// <summary>How to revert one change (journal entry).</summary>
public sealed record UndoRecord(UndoKind Kind)
{
    public string? Path { get; init; }
    /// <summary>Copy of a replaced file.</summary>
    public string? BackupPath { get; init; }
    public RegistryLocation? Registry { get; init; }
    public string? ValueName { get; init; }
    /// <summary>Value before the change (null: it did not exist).</summary>
    public RegValue? Previous { get; init; }
    /// <summary>Service or task name.</summary>
    public string? Name { get; init; }
    /// <summary>Environment variable entry.</summary>
    public string? Entry { get; init; }
}

public enum DeploymentState { InProgress, Deployed, RolledBack }

/// <summary>A deployment and its journal, kept to uninstall it.</summary>
public sealed class DeploymentRecord
{
    public required string Id { get; init; }
    public required string AppName { get; init; }
    public string? AppVersion { get; init; }
    public required DateTime DeployedUtc { get; init; }
    public required string PackagePath { get; init; }
    public string? InstallDir { get; init; }
    /// <summary>Target users whose hives may have to be mounted to uninstall.</summary>
    public required List<string> UserSids { get; init; }
    public DeploymentState State { get; set; }
    /// <summary>Changes in the order they were made.</summary>
    public List<UndoRecord> Journal { get; init; } = [];
    /// <summary>Problems that did not prevent the deployment (e.g. a service that could not be started).</summary>
    public List<string> Warnings { get; init; } = [];
}

public sealed class DeploymentException(string message, Exception inner, IReadOnlyList<string> rollbackFailures)
    : Exception(message, inner)
{
    /// <summary>Changes that could not be reverted.</summary>
    public IReadOnlyList<string> RollbackFailures => rollbackFailures;
}
