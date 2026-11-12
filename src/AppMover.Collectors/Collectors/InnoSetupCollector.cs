using AppMover.Core.Inno;
using AppMover.Core.Models;
using AppMover.Core.Text;
using static AppMover.Collectors.CollectionContext;
using static AppMover.Core.Inno.InnoUninstallLog;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// Everything Inno Setup recorded in its uninstall log (unins000.dat): files and folders outside the install
/// directory, Start menu entries, registry keys and values.
/// </summary>
public sealed class InnoSetupCollector : ICollector
{
    private const uint HkeyClassesRoot = 0x80000000;
    private const uint HkeyCurrentUser = 0x80000001;
    private const uint HkeyLocalMachine = 0x80000002;

    public string Name => "Inno Setup";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        if (context.App.Installer?.Properties.GetValueOrDefault(InstallerProperties.UninstallLog) is not { } path)
            yield break;

        InnoLog? log;
        try
        {
            using var stream = context.Files.OpenRead(path);
            log = Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log = null;
        }
        if (log is null)
            yield break;

        foreach (var record in log.Records.Where(r => r.Data.Count > 0))
        {
            var item = record.Type switch
            {
                DeleteFile => FileItem(context, record),
                DeleteDirOrFiles => DirectoryItem(context, record),
                DeleteGroupOrItem => StartMenuItem(context, record),
                RegDeleteEntireKey => RegistryItem(context, log, record, ItemKind.RegistryKey, Confidence.Certain, "Registry key removed by the uninstaller"),
                RegDeleteKeyIfEmpty => RegistryItem(context, log, record, ItemKind.RegistryKey, Confidence.Possible,
                    "Registry key created by the installer, removed when empty (may be shared)"),
                RegDeleteValue => RegistryItem(context, log, record, ItemKind.RegistryValue, Confidence.Certain, "Registry value removed by the uninstaller"),
                RegClearValue => RegistryItem(context, log, record, ItemKind.RegistryValue, Confidence.Probable, "Registry value cleared by the uninstaller"),
                _ => null,
            };
            if (item is not null)
                yield return item;
        }
    }

    private CollectedItem? FileItem(CollectionContext context, InnoLogRecord record)
    {
        var path = record.Data[0];
        if (context.IsInInstallRoot(path))
            return null;

        string? warning = null;
        if ((record.ExtraData & DeleteFileExistedBeforeInstall) != 0)
            warning = "The file existed before the installation.";
        else if ((record.ExtraData & DeleteFileIsFont) != 0)
            warning = "Font: it must be registered on the target machine.";
        else if ((record.ExtraData & DeleteFileSharedFile) != 0 || WinPath.IsUnderOrEqual(path, context.Session.Machine.Windows))
            warning = "Shared or system file: it may be used by other applications.";

        return Item(Name, ItemKind.File, ItemRole.Application, warning is null ? Confidence.Certain : Confidence.Possible,
            "File installed by Inno Setup", path: path, warning: warning);
    }

    /// <summary>Folders created by the installer, or files deleted by [UninstallDelete] (e.g. "C:\Data\*.log").</summary>
    private CollectedItem? DirectoryItem(CollectionContext context, InnoLogRecord record)
    {
        var isDirectory = (record.ExtraData & DeleteDirIsDir) != 0;
        var path = isDirectory ? record.Data[0] : WinPath.GetDirectoryName(record.Data[0]);
        if (path is null || WinPath.IsRoot(path) || context.IsInInstallRoot(path) || context.ContainsInstallRoot(path))
            return null;

        var extra = (record.ExtraData & DeleteDirExtra) != 0;
        return Item(Name, ItemKind.Directory, extra ? ItemRole.Data : ItemRole.Application, isDirectory ? Confidence.Certain : Confidence.Probable,
            extra ? "Data deleted by the uninstaller ([UninstallDelete])" : "Folder created by Inno Setup", path: path);
    }

    private CollectedItem? StartMenuItem(CollectionContext context, InnoLogRecord record)
    {
        var path = record.Data[0];
        var kind = context.Files.FileExists(path) ? ItemKind.File : context.Files.DirectoryExists(path) ? ItemKind.Directory : (ItemKind?)null;
        return kind is null ? null : Item(Name, kind.Value, ItemRole.Shortcut, Confidence.Certain, "Start menu entry created by Inno Setup", path: path);
    }

    private CollectedItem? RegistryItem(CollectionContext context, InnoLog log, InnoLogRecord record, ItemKind kind, Confidence confidence, string reason)
    {
        var root = (record.ExtraData & RegKeyHandleMask) switch
        {
            HkeyClassesRoot => 0,
            HkeyCurrentUser => 1,
            HkeyLocalMachine => 2,
            _ => -2,
        };
        var is64Bit = (record.ExtraData & Reg64BitKey) != 0;
        if (context.InstallerRegistryLocation(root, record.Data[0], is64Bit, log.IsAdminInstall) is not { } location)
            return null;
        if (kind == ItemKind.RegistryValue && record.Data.Count < 2)
            return null;
        return Item(Name, kind, ItemRole.Settings, confidence, reason, registry: location,
            valueName: kind == ItemKind.RegistryValue ? record.Data[1] : null);
    }
}
