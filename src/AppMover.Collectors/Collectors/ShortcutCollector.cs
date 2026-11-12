using AppMover.Core.Models;
using AppMover.Core.Shell;
using AppMover.Core.Text;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>Start menu, desktop and taskbar shortcuts pointing to the application.</summary>
public sealed class ShortcutCollector : ICollector
{
    private const int MaxShortcutSize = 1024 * 1024;

    public string Name => "Shortcuts";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        var machine = context.Session.Machine;
        var locations = new List<(string Path, SessionUser? User)>
        {
            (machine.CommonStartMenu, null),
            (machine.CommonDesktop, null),
        };
        foreach (var user in context.Session.Users)
        {
            locations.Add((user.Folders.StartMenu, user));
            locations.Add((user.Folders.Desktop, user));
            locations.Add((WinPath.Combine(user.Folders.AppData, @"Microsoft\Internet Explorer\Quick Launch"), user));
        }

        var productCodes = context.MsiProducts.Select(p => p.Registration.ProductCode)
            .Append(context.App.Installer?.Properties.GetValueOrDefault(InstallerProperties.ProductCode))
            .OfType<string>()
            .ToList();

        foreach (var (location, user) in locations)
        {
            var files = context.Files.EnumerateFiles(location, "*.lnk", recursive: true)
                .Select(file => (File: file, Item: Evaluate(context, file, user, productCodes)))
                .ToList();
            var matched = files.Where(f => f.Item is not null).Select(f => f.Item!).ToList();

            // A Start menu sub-folder whose shortcuts all belong to the app is taken as a whole (empty folders are recreated too).
            var programs = WinPath.Combine(location, "Programs");
            var ownedFolders = matched
                .SelectMany(m => Ancestors(m.Path!, programs))
                .Distinct(WinPath.Comparer)
                .Where(folder => files.Where(f => WinPath.IsUnderOrEqual(f.File, folder)).All(f => f.Item is not null))
                .ToList();
            foreach (var folder in ownedFolders)
            {
                var weakest = matched.Where(m => WinPath.IsUnderOrEqual(m.Path!, folder)).Min(m => m.Confidence);
                yield return Item(Name, ItemKind.Directory, ItemRole.Shortcut, weakest, "Start menu folder containing only the application's shortcuts", path: folder, user: user);
            }
            foreach (var item in matched)
                yield return item;
        }
    }

    /// <summary>Folders between the file and <paramref name="top"/> (both excluded).</summary>
    private static IEnumerable<string> Ancestors(string file, string top)
    {
        if (!WinPath.IsUnderOrEqual(file, top))
            yield break;
        for (var dir = WinPath.GetDirectoryName(file); dir is not null && dir.Length > WinPath.Normalize(top).Length; dir = WinPath.GetDirectoryName(dir))
            yield return dir;
    }

    private CollectedItem? Evaluate(CollectionContext context, string file, SessionUser? user, IReadOnlyList<string> productCodes)
    {
        var link = Read(context, file);
        if (link is null)
            return null;

        var details = new Dictionary<string, string>();
        void Detail(string key, string? value)
        {
            if (value is not null)
                details[key] = value;
        }
        Detail("Target", link.Target);
        Detail("Arguments", link.Arguments);
        Detail("WorkingDirectory", link.WorkingDirectory);
        Detail("IconLocation", link.IconLocation);
        Detail("DarwinId", link.DarwinId);

        string? reason = null;
        var confidence = Confidence.Certain;
        if (context.FindReferencedRoot(link.Target, user) is { } root)
            reason = $"Target is in {root}";
        else if (link.IconLocation is { } icon && productCodes.Any(code => icon.Contains(code, StringComparison.OrdinalIgnoreCase)))
            reason = "Advertised Windows Installer shortcut of this product";
        else if (link.Target is null && link.DarwinId is not null
                 && context.Keywords.Match(WinPath.ChangeExtension(WinPath.GetFileName(file), "")) is KeywordMatch.Product or KeywordMatch.ProductAlias)
        {
            reason = "Advertised shortcut named after the application";
            confidence = Confidence.Probable;
        }

        return reason is null
            ? null
            : Item(Name, ItemKind.File, ItemRole.Shortcut, confidence, reason, path: file, user: user, details: details);
    }

    private static ShellLinkInfo? Read(CollectionContext context, string file)
    {
        try
        {
            using var stream = context.Files.OpenRead(file);
            if (stream.Length > MaxShortcutSize)
                return null;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return ShellLink.Parse(memory.GetBuffer().AsSpan(0, (int)memory.Length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
