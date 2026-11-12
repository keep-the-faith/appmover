using System.Text.RegularExpressions;
using AppMover.Core.Models;
using AppMover.Core.Msi;
using AppMover.Core.Text;

namespace AppMover.Collectors;

/// <summary>Finds the install directories of an application from its Uninstall entry and installer data.</summary>
public static partial class InstallRootResolver
{
    public static IReadOnlyList<InstallRoot> Resolve(CollectionContext context)
    {
        var app = context.App;
        var owner = context.Session.FindUser(app.UserSid);
        var candidates = new List<(string Path, Confidence Confidence, string Reason)>();

        void Add(string? path, Confidence confidence, string reason)
        {
            if (!string.IsNullOrWhiteSpace(path))
                candidates.Add((WinPath.Normalize(context.Expand(path, owner)), confidence, reason));
        }

        Add(app.InstallLocation, Confidence.Certain, "InstallLocation of the Uninstall entry");
        if (app.Installer?.Properties.GetValueOrDefault(InstallerProperties.InstallerAppPath) is { } innoPath)
            Add(innoPath, Confidence.Certain, "Install path declared by Inno Setup");
        foreach (var msi in context.MsiProducts)
            Add(msi.Registration.InstallLocation, Confidence.Certain, "InstallLocation registered by Windows Installer");
        if (app.DisplayIcon is { } icon && !IsInstallerCache(icon))
            Add(WinPath.GetDirectoryName(StripIconIndex(icon)) ?? "", Confidence.Probable, "Folder of the DisplayIcon");
        if (app.Installer?.UninstallerPath is { } uninstaller && !IsInstallerCache(uninstaller))
            Add(WinPath.GetDirectoryName(uninstaller), Confidence.Probable, "Folder of the uninstaller");
        foreach (var msi in context.MsiProducts)
        {
            foreach (var folder in ComponentFolders(context, msi))
                Add(folder, Confidence.Probable, "Common folder of the Windows Installer components");
        }

        var accepted = candidates
            .Where(c => IsAcceptable(c.Path, context))
            .ToList();

        // Merge nested folders into their parent, keeping the best confidence. The first candidate is the primary one.
        var roots = new List<(string Path, Confidence Confidence, string Reason, bool Primary)>();
        foreach (var (candidate, index) in accepted.Select((c, i) => (c, i)).OrderBy(x => x.c.Path.Length))
        {
            var parent = roots.FindIndex(r => WinPath.IsUnderOrEqual(candidate.Path, r.Path));
            if (parent < 0)
            {
                roots.Add((candidate.Path, candidate.Confidence, candidate.Reason, index == 0));
                continue;
            }
            var r = roots[parent];
            roots[parent] = (r.Path, (Confidence)Math.Max((int)r.Confidence, (int)candidate.Confidence), r.Reason, r.Primary || index == 0);
        }

        return roots.Select(r => new InstallRoot(r.Path, r.Confidence, r.Reason, r.Primary)).ToList();
    }

    /// <summary>
    /// Folders holding the components of an MSI product: per known folder (Program Files, AppData…), the common folder
    /// of its components, or each component folder when the common folder is the known folder itself (nested folders are
    /// merged afterwards). Components in the Windows folder or shared with other products never define application folders.
    /// </summary>
    private static IEnumerable<string> ComponentFolders(CollectionContext context, MsiProduct msi)
    {
        var machine = context.Session.Machine;
        var known = machine.All().Select(f => f.Path)
            .Concat(context.Session.Users.SelectMany(u => u.Folders.All().Select(f => f.Path)))
            .ToList();

        var folders = msi.Components
            .Where(c => !c.Shared)
            .Select(c => c.KeyPath.Kind switch
            {
                MsiKeyPathKind.File => WinPath.GetDirectoryName(c.KeyPath.Path!),
                MsiKeyPathKind.Directory => c.KeyPath.Path,
                _ => null,
            })
            .OfType<string>()
            .Select(WinPath.Normalize)
            .Where(f => !WinPath.IsUnderOrEqual(f, machine.Windows))
            .Distinct(WinPath.Comparer);

        // Group by the deepest known folder containing them (or the drive).
        var groups = folders.GroupBy(f =>
            known.Where(k => WinPath.IsUnderOrEqual(f, k)).OrderByDescending(k => k.Length).FirstOrDefault() ?? f[..3], WinPath.Comparer);

        foreach (var group in groups)
        {
            string? common = group.First();
            foreach (var folder in group.Skip(1))
                common = CommonFolder(common, folder);
            if (common is not null && !WinPath.IsUnderOrEqual(group.Key, common))
            {
                yield return common;
                continue;
            }
            foreach (var folder in group.Where(f => !f.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
                yield return folder;
        }
    }

    private static string? CommonFolder(string? a, string b)
    {
        if (a is null)
            return null;
        var left = a.Split('\\');
        var right = b.Split('\\');
        var count = 0;
        while (count < left.Length && count < right.Length && left[count].Equals(right[count], StringComparison.OrdinalIgnoreCase))
            count++;
        return count == 0 ? null : WinPath.Normalize(string.Join('\\', left[..count]));
    }

    /// <summary>Rejects missing folders and folders that are too broad (drive root, Program Files, a user profile…).</summary>
    private static bool IsAcceptable(string path, CollectionContext context)
    {
        if (WinPath.IsRoot(path) || !context.Files.DirectoryExists(path))
            return false;
        var machine = context.Session.Machine;
        if (WinPath.IsUnderOrEqual(path, machine.Windows))
            return false;
        var known = machine.All().Select(f => f.Path)
            .Concat(context.Session.Users.SelectMany(u => u.Folders.All().Select(f => f.Path)))
            .Concat([WinPath.Combine(machine.ProgramFiles, "Common Files"), WinPath.Combine(machine.ProgramFilesX86, "Common Files")]);
        // A folder that is (or contains) a known folder is too broad.
        return !known.Any(k => WinPath.IsUnderOrEqual(k, path));
    }

    /// <summary>Installer caches hold a copy of the setup, not the application.</summary>
    private static bool IsInstallerCache(string path) =>
        path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\InstallShield Installation Information\", StringComparison.OrdinalIgnoreCase);

    /// <summary>"C:\App\app.exe,0" → "C:\App\app.exe".</summary>
    public static string StripIconIndex(string icon) => IconIndex().Replace(icon.Trim().Trim('"'), "").Trim('"');

    [GeneratedRegex(@",\s*-?\d+\s*$")]
    private static partial Regex IconIndex();
}
