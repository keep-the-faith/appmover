using System.Security;
using System.Security.Cryptography;
using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Text;
using Microsoft.Extensions.Logging;

namespace AppMover.Deployment;

/// <summary>
/// Step 5: applies a plan. Every change is journaled before it is made, so that a failure rolls everything back
/// and the deployment can be uninstalled later.
/// </summary>
public sealed class DeploymentExecutor(
    IRegistrySource registry, IRegistryWriter registryWriter, IWritableFileSystem files, SystemServices system,
    DeploymentStore store, ILogger<DeploymentExecutor> logger)
{
    /// <summary>The journal is saved after each change, except files: every N files.</summary>
    private const int SaveEveryFiles = 100;

    public DeploymentRecord Deploy(DeploymentPlan plan, PackageReader package, CollectionSession target, IProgress<string>? progress = null)
    {
        if (!plan.CanProceed)
            throw new InvalidOperationException("The deployment plan has blocking errors.");

        var record = new DeploymentRecord
        {
            Id = plan.Id,
            AppName = plan.Manifest.App.DisplayName ?? plan.Manifest.App.KeyName,
            AppVersion = plan.Manifest.App.DisplayVersion,
            DeployedUtc = DateTime.UtcNow,
            PackagePath = package.Path,
            InstallDir = plan.InstallDir,
            UserSids = plan.UserSids.ToList(),
            State = DeploymentState.InProgress,
        };
        store.Save(record);

        var context = new ApplyContext(record, plan, package, target, store.GetBackupDirectory(record.Id));
        try
        {
            var filesSinceSave = 0;
            foreach (var operation in plan.Operations)
            {
                progress?.Report(operation.Describe());
                Apply(operation, context);
                if (operation is not ExtractFileOperation || ++filesSinceSave % SaveEveryFiles == 0)
                    store.Save(record);
            }
            record.State = DeploymentState.Deployed;
            StartServices(plan, record, progress);
            store.Save(record);
            logger.LogInformation("Deployment {Id} of {App}: {Changes} change(s)", record.Id, record.AppName, record.Journal.Count);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Deployment {Id} failed, rolling back", record.Id);
            var failures = Revert(record, target);
            record.State = DeploymentState.RolledBack;
            store.Save(record);
            throw new DeploymentException($"Deployment failed and was rolled back: {e.Message}", e, failures);
        }
        finally
        {
            Notify(context.EnvironmentChanged, context.AssociationsChanged);
        }
        return record;
    }

    /// <summary>
    /// Starts the created services that start with Windows, as their installer did; otherwise they would only run after
    /// a reboot. A failure is reported, not rolled back: the application is installed.
    /// </summary>
    private void StartServices(DeploymentPlan plan, DeploymentRecord record, IProgress<string>? progress)
    {
        foreach (var spec in plan.Operations.OfType<CreateServiceOperation>().Select(o => o.Spec).Where(s => s.StartsWithWindows))
        {
            progress?.Report($"Start service   {spec.Name}");
            try
            {
                system.Services.Start(spec.Name);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Service {Service} could not be started", spec.Name);
                record.Warnings.Add($"Service '{spec.Name}' could not be started ({e.Message}): start it from services.msc or restart the computer.");
            }
        }
    }

    /// <summary>Reverts a deployment and deletes its journal. Returns the changes that could not be reverted.</summary>
    public IReadOnlyList<string> Uninstall(DeploymentRecord record, CollectionSession target)
    {
        var failures = Revert(record, target);
        if (failures.Count == 0)
            store.Delete(record.Id);
        else
            store.Save(record); // keeps the journal of what is left, to try again
        return failures;
    }

    private sealed class ApplyContext(DeploymentRecord record, DeploymentPlan plan, PackageReader package, CollectionSession target,
        string backupDirectory)
    {
        public DeploymentRecord Record => record;
        public DeploymentPlan Plan => plan;
        public PackageReader Package => package;
        public CollectionSession Target => target;
        public string BackupDirectory => backupDirectory;
        public int Backups { get; set; }
        public bool EnvironmentChanged { get; set; }
        public bool AssociationsChanged { get; set; }
        public void Journal(UndoRecord undo) => record.Journal.Add(undo);
    }

    private void Apply(DeployOperation operation, ApplyContext context)
    {
        switch (operation)
        {
            case CreateDirectoryOperation op:
                EnsureDirectory(op.Path, context);
                break;
            case ExtractFileOperation op:
                PrepareFile(op.Target, context);
                Extract(op, context);
                break;
            case CreateShortcutOperation op:
                PrepareFile(op.Path, context);
                system.Shortcuts.Create(op.Path, op.Shortcut);
                break;
            case CreateServiceOperation op:
                context.Journal(new UndoRecord(UndoKind.ServiceCreated) { Name = op.Spec.Name });
                system.Services.Create(op.Spec);
                break;
            case WriteRegistryOperation op:
                EnsureKey(op.Location, context);
                foreach (var value in op.Values)
                    SetValue(op.Location, value, context);
                context.AssociationsChanged |= op.Location.Path.Contains(@"\Classes", StringComparison.OrdinalIgnoreCase);
                context.EnvironmentChanged |= op.Location.Path.EndsWith("Environment", StringComparison.OrdinalIgnoreCase);
                break;
            case AddPathEntryOperation op:
                AddPathEntry(op, context);
                break;
            case RegisterTaskOperation op:
                context.Journal(new UndoRecord(UndoKind.TaskRegistered) { Name = op.Name });
                system.Tasks.Register(op.Name, op.Xml);
                break;
            case WriteUninstallEntryOperation op:
                WriteUninstallEntry(op, context);
                break;
            default:
                throw new NotSupportedException(operation.GetType().Name);
        }
    }

    private void EnsureDirectory(string path, ApplyContext context)
    {
        var missing = new Stack<string>();
        for (var dir = WinPath.Normalize(path); !WinPath.IsRoot(dir) && !files.DirectoryExists(dir); dir = WinPath.GetDirectoryName(dir)!)
            missing.Push(dir);
        while (missing.Count > 0)
        {
            var dir = missing.Pop();
            context.Journal(new UndoRecord(UndoKind.DirectoryCreated) { Path = dir });
            files.CreateDirectory(dir);
        }
    }

    /// <summary>Creates the parent folders and backs up the file it replaces.</summary>
    private void PrepareFile(string path, ApplyContext context)
    {
        EnsureDirectory(WinPath.GetDirectoryName(path)!, context);
        if (!files.FileExists(path))
        {
            context.Journal(new UndoRecord(UndoKind.FileCreated) { Path = path });
            return;
        }

        files.CreateDirectory(context.BackupDirectory);
        var backup = WinPath.Combine(context.BackupDirectory, $"{++context.Backups:D6}_{WinPath.GetFileName(path)}");
        files.Copy(path, backup);
        context.Journal(new UndoRecord(UndoKind.FileReplaced) { Path = path, BackupPath = backup });
    }

    private void Extract(ExtractFileOperation op, ApplyContext context)
    {
        using (var input = context.Package.OpenEntry(op.File.Entry))
        using (var output = files.Create(op.Target))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            if (op.File.RewritePaths)
            {
                // Configuration file: checked as packaged, then its paths are adapted to this machine.
                using var memory = new MemoryStream();
                input.CopyTo(memory);
                var bytes = memory.ToArray();
                hash.AppendData(bytes);
                CheckHash(hash, op.File);
                if (TextCodec.Decode(bytes) is { } text && context.Plan.RelocateText(text.Text) is var relocated && relocated != text.Text)
                    bytes = TextCodec.Encode(text, relocated);
                output.Write(bytes);
            }
            else
            {
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
                CheckHash(hash, op.File);
            }
        }
        files.SetMeta(op.Target, op.File.LastWriteUtc, op.File.Attributes);
    }

    private static void CheckHash(IncrementalHash hash, Packaging.PackageFile file)
    {
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Corrupted file in the package (SHA-256 mismatch): {file.Path}");
    }

    /// <summary>Creates the key and journals the highest key that did not exist.</summary>
    private void EnsureKey(RegistryLocation location, ApplyContext context)
    {
        var segments = location.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= segments.Length; i++)
        {
            var sub = location with { Path = string.Join('\\', segments[..i]) };
            using var key = context.Target.OpenKey(registry, sub);
            if (key is not null)
                continue;

            context.Journal(new UndoRecord(UndoKind.RegistryKeyCreated) { Registry = sub });
            var physical = Physical(location, context.Target);
            registryWriter.CreateKey(physical.Hive, physical.View, physical.Path);
            return;
        }
    }

    private void SetValue(RegistryLocation location, RegValue value, ApplyContext context)
    {
        RegValue? previous;
        bool keyExisted;
        using (var key = context.Target.OpenKey(registry, location))
        {
            keyExisted = key is not null;
            previous = key?.GetValue(value.Name);
        }
        if (previous is not null && previous.SameData(value))
            return;

        var undo = new UndoRecord(UndoKind.RegistryValueSet) { Registry = location, ValueName = value.Name, Previous = previous };
        context.Journal(undo);
        var physical = Physical(location, context.Target);
        try
        {
            registryWriter.SetValue(physical.Hive, physical.View, physical.Path, value);
        }
        catch (Exception e) when (keyExisted && e is UnauthorizedAccessException or SecurityException)
        {
            // A protected key of this PC (owned by Windows or another application, e.g. a browser's
            // NativeMessagingHosts\com.microsoft.browsercore): left as is rather than failing the whole deployment.
            context.Record.Journal.Remove(undo);
            logger.LogWarning(e, "Registry value {Location} → {Name} not written: access denied", location, value.Name);
            context.Record.Warnings.Add($"Registry value not written (access denied, the key is protected on this PC): {location} → {value.Name}");
        }
    }

    private void DeleteValue(RegistryLocation location, string name, ApplyContext context)
    {
        RegValue? previous;
        using (var key = context.Target.OpenKey(registry, location))
            previous = key?.GetValue(name);
        if (previous is null)
            return;
        context.Journal(new UndoRecord(UndoKind.RegistryValueSet) { Registry = location, ValueName = name, Previous = previous });
        var physical = Physical(location, context.Target);
        registryWriter.DeleteValue(physical.Hive, physical.View, physical.Path, name);
    }

    private void AddPathEntry(AddPathEntryOperation op, ApplyContext context)
    {
        RegValue? current;
        using (var key = context.Target.OpenKey(registry, op.Location))
            current = key?.GetValue(op.Variable);
        var text = current?.AsString() ?? "";
        var entries = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Contains(op.Entry, StringComparer.OrdinalIgnoreCase))
            return;

        EnsureKey(op.Location, context);
        context.Journal(new UndoRecord(UndoKind.PathEntryAdded) { Registry = op.Location, ValueName = op.Variable, Entry = op.Entry });
        var updated = text.Length == 0 ? op.Entry : text.TrimEnd(';') + ";" + op.Entry;
        var physical = Physical(op.Location, context.Target);
        registryWriter.SetValue(physical.Hive, physical.View, physical.Path,
            new RegValue { Name = op.Variable, Type = current?.Type ?? RegValueType.ExpandString, Text = updated });
        context.EnvironmentChanged = true;
    }

    /// <summary>Points "Apps &amp; features" to AppMover: the original uninstaller does not know this machine.</summary>
    private void WriteUninstallEntry(WriteUninstallEntryOperation op, ApplyContext context)
    {
        EnsureKey(op.Location, context);
        SetValue(op.Location, RegValue.String("UninstallString", op.Command), context);
        SetValue(op.Location, RegValue.String("QuietUninstallString", op.Command + " --yes"), context);
        SetValue(op.Location, RegValue.DWord("NoModify", 1), context);
        SetValue(op.Location, RegValue.DWord("NoRepair", 1), context);
        SetValue(op.Location, RegValue.DWord("WindowsInstaller", 0), context);
        SetValue(op.Location, RegValue.String("AppMoverDeploymentId", context.Record.Id), context);
        DeleteValue(op.Location, "ModifyPath", context);
    }

    /// <summary>Undoes the journal from the last change to the first. Returns what failed.</summary>
    private List<string> Revert(DeploymentRecord record, CollectionSession target)
    {
        var failures = new List<string>();
        var remaining = new List<UndoRecord>();
        var environment = false;
        var associations = false;

        for (var i = record.Journal.Count - 1; i >= 0; i--)
        {
            var undo = record.Journal[i];
            try
            {
                Undo(undo, target);
                environment |= undo.Kind == UndoKind.PathEntryAdded || undo.Registry?.Path.EndsWith("Environment", StringComparison.OrdinalIgnoreCase) == true;
                associations |= undo.Registry?.Path.Contains(@"\Classes", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Cannot undo {Kind} {Target}", undo.Kind, Describe(undo));
                failures.Add($"{undo.Kind} {Describe(undo)}: {e.Message}");
                remaining.Insert(0, undo);
            }
        }

        record.Journal.Clear();
        record.Journal.AddRange(remaining);
        Notify(environment, associations);
        return failures;
    }

    private bool SameContent(string a, string b)
    {
        if (files.GetFileMeta(a) is not { } metaA || files.GetFileMeta(b) is not { } metaB || metaA.Length != metaB.Length)
            return false;
        using var streamA = files.OpenRead(a);
        using var streamB = files.OpenRead(b);
        return SHA256.HashData(streamA).AsSpan().SequenceEqual(SHA256.HashData(streamB));
    }

    private void Undo(UndoRecord undo, CollectionSession target)
    {
        switch (undo.Kind)
        {
            case UndoKind.DirectoryCreated:
                files.DeleteDirectory(undo.Path!); // left in place when not empty (user files)
                break;
            case UndoKind.FileCreated:
                if (files.FileExists(undo.Path!))
                    files.DeleteFile(undo.Path!);
                break;
            case UndoKind.FileReplaced:
                // Not restored when unchanged: the write may have failed before touching it (file in use).
                if (!SameContent(undo.BackupPath!, undo.Path!))
                    files.Copy(undo.BackupPath!, undo.Path!);
                break;
            case UndoKind.RegistryKeyCreated:
            {
                var physical = Physical(undo.Registry!, target);
                registryWriter.DeleteKeyTree(physical.Hive, physical.View, physical.Path);
                break;
            }
            case UndoKind.RegistryValueSet:
            {
                using (var key = target.OpenKey(registry, undo.Registry!))
                {
                    if (key is null)
                        break; // the key was deleted meanwhile (e.g. with its service)
                    var current = key.GetValue(undo.ValueName!);
                    if (current is null ? undo.Previous is null : undo.Previous is not null && current.SameData(undo.Previous))
                        break; // already as it was (the write failed or was never done)
                }
                var physical = Physical(undo.Registry!, target);
                if (undo.Previous is null)
                    registryWriter.DeleteValue(physical.Hive, physical.View, physical.Path, undo.ValueName!);
                else
                    registryWriter.SetValue(physical.Hive, physical.View, physical.Path, undo.Previous);
                break;
            }
            case UndoKind.ServiceCreated:
                if (system.Services.Exists(undo.Name!))
                    system.Services.Delete(undo.Name!);
                break;
            case UndoKind.TaskRegistered:
                if (system.Tasks.Exists(undo.Name!))
                    system.Tasks.Delete(undo.Name!);
                break;
            case UndoKind.PathEntryAdded:
            {
                RegValue? current;
                using (var key = target.OpenKey(registry, undo.Registry!))
                    current = key?.GetValue(undo.ValueName!);
                if (current?.AsString() is not { } text)
                    break;
                var entries = text.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(e => !e.Trim().Equals(undo.Entry, StringComparison.OrdinalIgnoreCase));
                var physical = Physical(undo.Registry!, target);
                registryWriter.SetValue(physical.Hive, physical.View, physical.Path, current with { Text = string.Join(';', entries) });
                break;
            }
        }
    }

    private static (RegHive Hive, RegView View, string Path) Physical(RegistryLocation location, CollectionSession target) =>
        target.Resolve(location) ?? throw new InvalidOperationException($"User {location.UserSid} is not available on this machine.");

    private static string Describe(UndoRecord undo) =>
        undo.Path ?? undo.Name ?? (undo.Registry is null ? "" : $"{undo.Registry}{(undo.ValueName is null ? "" : $" → {undo.ValueName}")}");

    private void Notify(bool environment, bool associations)
    {
        try
        {
            if (environment)
                system.Notifier.EnvironmentChanged();
            if (associations)
                system.Notifier.AssociationsChanged();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Cannot notify the system of the changes");
        }
    }
}
