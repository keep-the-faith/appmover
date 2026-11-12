using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Text;

namespace AppMover.Detection;

public sealed class DetectionContext(IRegistrySource registry, IFileProbe files, IPathResolver paths)
{
    /// <summary>Binary signatures searched in uninstaller executables.</summary>
    public static class Markers
    {
        public const string NsisOverlay = "NullsoftInst";
        public const string NsisManifest = "Nullsoft.NSIS.exehead";
        public const string InstallShield = "InstallShield";
        public const string Squirrel = "Squirrel";
        public const string InnoSetup = "Inno Setup";

        public static IReadOnlyCollection<string> All { get; } = [NsisOverlay, NsisManifest, InstallShield, Squirrel, InnoSetup];
    }

    /// <summary>Signatures are located in the first megabytes (stub, resources, start of the overlay).</summary>
    public const long MaxScanBytes = 8L * 1024 * 1024;

    /// <summary>System executables used as uninstallers, never scanned.</summary>
    private static readonly string[] SystemLaunchers = ["msiexec.exe", "rundll32.exe", "cmd.exe", "powershell.exe", "wscript.exe"];

    private readonly Dictionary<string, IReadOnlySet<string>> _markerCache = new(StringComparer.OrdinalIgnoreCase);

    public IRegistrySource Registry => registry;
    public IFileProbe Files => files;
    public IPathResolver Paths => paths;

    /// <summary>Expanded uninstaller executable (from UninstallString, then QuietUninstallString).</summary>
    public string? GetUninstallerPath(InstalledApp app)
    {
        var exe = CommandLine.ExtractExecutable(app.UninstallString) ?? CommandLine.ExtractExecutable(app.QuietUninstallString);
        return exe is null ? null : paths.Expand(exe);
    }

    /// <summary>True when the uninstaller is a system program (msiexec, rundll32…) rather than a vendor executable.</summary>
    public static bool IsSystemLauncher(string path) => SystemLaunchers.Any(l => WinPath.FileNameEquals(path, l));

    /// <summary>Signatures found in the vendor uninstaller (scanned once per file).</summary>
    public IReadOnlySet<string> GetUninstallerMarkers(InstalledApp app)
    {
        var path = GetUninstallerPath(app);
        if (path is null || IsSystemLauncher(path))
            return new HashSet<string>();

        if (!_markerCache.TryGetValue(path, out var markers))
        {
            markers = files.FindMarkers(path, Markers.All, MaxScanBytes);
            _markerCache[path] = markers;
        }
        return markers;
    }

    /// <summary>All the command lines of the entry that may reveal the installer technology.</summary>
    public static IEnumerable<string> GetCommandLines(InstalledApp app)
    {
        foreach (var name in new[] { "UninstallString", "QuietUninstallString", "ModifyPath" })
        {
            if (app.RawValues.TryGetValue(name, out var value) && value.AsString() is { Length: > 0 } text)
                yield return text;
        }
    }

    public static bool AnyCommandLineContains(InstalledApp app, string text) =>
        GetCommandLines(app).Any(c => c.Contains(text, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Clues collected by one rule.</summary>
public sealed class DetectionCandidate(InstallerType type)
{
    private readonly List<Evidence> _evidence = [];
    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);

    public InstallerType Type => type;
    public IReadOnlyList<Evidence> Evidence => _evidence;
    public IReadOnlyDictionary<string, string> Properties => _properties;
    public int Score => Math.Min(100, _evidence.Sum(e => e.Weight));

    public DetectionCandidate Add(string description, int weight)
    {
        _evidence.Add(new Evidence(description, weight));
        return this;
    }

    public DetectionCandidate Set(string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            _properties[key] = value;
        return this;
    }

    /// <summary>The candidate when at least one clue was found, null otherwise.</summary>
    public DetectionCandidate? NullIfEmpty() => _evidence.Count == 0 ? null : this;
}

public interface IDetectionRule
{
    InstallerType Type { get; }

    /// <summary>Returns the clues found, or null when nothing matches.</summary>
    DetectionCandidate? Evaluate(InstalledApp app, DetectionContext context);
}
