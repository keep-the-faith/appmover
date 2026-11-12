using AppMover.Core.Models;
using AppMover.Core.Text;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// Data folders and settings keys the application actually used while it was traced (dynamic analysis):
/// the first folder under AppData, ProgramData… and the Software\Vendor\Product keys it wrote.
/// </summary>
public sealed class UsageTraceCollector : ICollector
{
    /// <summary>Shared folders and keys every program touches: never attributed to one application.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Temp", "Packages", "CrashDumps", "D3DSCache", "NVIDIA", "NVIDIA Corporation", "Intel", "AMD", "Classes",
        "Policies", "WOW6432Node", "Windows", "IconCache.db", "desktop.ini", "Package Cache", "regid.1991-06.com.microsoft",
    };

    public string Name => "Usage trace";

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        if (context.UsageTrace is not { } trace)
            return [];

        var bases = Bases(context);
        var files = trace.FilesWritten.Select(f => (Path: f, Written: true))
            .Concat(trace.FilesRead.Select(f => (Path: f, Written: false)));
        return FileItems(context, bases, files).Concat(RegistryItems(context, trace.RegistryKeysWritten));
    }

    /// <summary>Folders whose sub-folders are per-application, with the best confidence a used sub-folder can get.</summary>
    private static List<(string Path, SessionUser? User, Confidence Max)> Bases(CollectionContext context)
    {
        var machine = context.Session.Machine;
        var bases = new List<(string, SessionUser?, Confidence)> { (machine.ProgramData, null, Confidence.Probable), (machine.Public, null, Confidence.Possible) };
        foreach (var user in context.Session.Users)
        {
            bases.Add((user.Folders.AppData, user, Confidence.Probable));
            bases.Add((user.Folders.LocalAppData, user, Confidence.Probable));
            bases.Add((user.Folders.LocalAppDataLow, user, Confidence.Probable));
            bases.Add((user.Folders.Documents, user, Confidence.Possible));
        }
        // Deepest first: LocalAppData before the profile, Documents before Public…
        return bases.OrderByDescending(b => b.Item1.Length).ToList();
    }

    private IEnumerable<CollectedItem> FileItems(CollectionContext context, List<(string Path, SessionUser? User, Confidence Max)> bases,
        IEnumerable<(string Path, bool Written)> files)
    {
        var best = new Dictionary<string, (ItemKind Kind, Confidence Confidence, SessionUser? User, bool Written)>(WinPath.Comparer);
        foreach (var (raw, written) in files)
        {
            var path = WinPath.Normalize(raw);
            if (path.Length < 3 || path[1] != ':' || context.IsInInstallRoot(path) || WinPath.IsUnderOrEqual(path, context.Session.Machine.Windows))
                continue;
            var match = bases.FirstOrDefault(b => WinPath.IsUnderOrEqual(path, b.Path) && !b.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (match.Path is null)
                continue;

            var first = path[(match.Path.Length + 1)..].Split('\\')[0];
            if (Noise.Contains(first))
                continue;
            var candidate = WinPath.Combine(match.Path, first);
            if (context.ContainsInstallRoot(candidate))
                continue;
            var kind = candidate.Equals(path, StringComparison.OrdinalIgnoreCase) && !context.Files.DirectoryExists(path) ? ItemKind.File : ItemKind.Directory;
            // Reading a folder is a weaker clue than writing to it.
            var confidence = written ? match.Max : Confidence.Possible;
            if (!best.TryGetValue(candidate, out var current) || confidence > current.Confidence)
                best[candidate] = (kind, confidence, match.User, written);
        }

        foreach (var (path, found) in best)
        {
            if (found.Kind == ItemKind.File ? !context.Files.FileExists(path) : !context.Files.DirectoryExists(path))
                continue;
            yield return Item(Name, found.Kind, ItemRole.Data, found.Confidence,
                found.Written ? "Written by the application while it was used" : "Read by the application while it was used",
                path: path, user: found.User);
        }
    }

    private IEnumerable<CollectedItem> RegistryItems(CollectionContext context, IEnumerable<string> keys)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var native in keys)
        {
            if (ToLocation(context, native) is not { } location || !seen.Add(location.ToString()))
                continue;
            yield return Item(Name, ItemKind.RegistryKey, ItemRole.Settings, Confidence.Probable,
                "Written by the application while it was used", registry: location);
        }
    }

    /// <summary>
    /// \REGISTRY\MACHINE\SOFTWARE\[WOW6432Node\]Vendor\Product\… or \REGISTRY\USER\&lt;SID&gt;\Software\Vendor\Product\…
    /// → the Vendor\Product key (or Vendor alone), null for shared or unknown keys.
    /// </summary>
    private static RegistryLocation? ToLocation(CollectionContext context, string native)
    {
        var parts = native.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts[0].Equals("REGISTRY", StringComparison.OrdinalIgnoreCase))
            return null;

        bool machine;
        string? sid = null;
        int softwareIndex;
        if (parts[1].Equals("MACHINE", StringComparison.OrdinalIgnoreCase))
        {
            machine = true;
            softwareIndex = 2;
        }
        else if (parts[1].Equals("USER", StringComparison.OrdinalIgnoreCase) && context.Session.FindUser(parts[2]) is not null)
        {
            machine = false;
            sid = parts[2];
            softwareIndex = 3;
        }
        else
        {
            return null;
        }
        if (parts.Length <= softwareIndex + 1 || !parts[softwareIndex].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = parts[(softwareIndex + 1)..].ToList();
        var view = Core.Registry.RegView.Registry64;
        if (machine && rest[0].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
        {
            view = Core.Registry.RegView.Registry32;
            rest.RemoveAt(0);
        }
        if (rest.Count == 0 || Noise.Contains(rest[0]))
            return null;

        var key = (machine ? "SOFTWARE\\" : "Software\\") + string.Join('\\', rest.Take(2));
        return machine ? RegistryLocation.Machine(view, key) : RegistryLocation.User(sid!, key);
    }
}
