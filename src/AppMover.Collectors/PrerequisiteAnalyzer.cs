using System.Text.Json;
using AppMover.Core.Models;
using AppMover.Core.Pe;
using AppMover.Core.Platform;
using AppMover.Core.Text;

namespace AppMover.Collectors;

/// <summary>Finds the runtimes an application needs from the DLLs its executables import and its .NET configuration.</summary>
public static class PrerequisiteAnalyzer
{
    private const int MaxBinaries = 3000;
    private const long MaxBinarySize = 256L * 1024 * 1024;

    private static readonly HashSet<string> VcRuntime140 = new(StringComparer.OrdinalIgnoreCase)
    {
        "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "msvcp140_atomic_wait.dll",
        "msvcp140_codecvt_ids.dll", "concrt140.dll", "vccorlib140.dll", "mfc140.dll", "mfc140u.dll", "vcomp140.dll",
    };

    /// <summary>DLL prefix → Visual C++ version (year).</summary>
    private static readonly (string Prefix, string Year)[] VcRuntimeLegacy =
    [
        ("msvcr120", "2013"), ("msvcp120", "2013"), ("mfc120", "2013"),
        ("msvcr110", "2012"), ("msvcp110", "2012"), ("mfc110", "2012"),
        ("msvcr100", "2010"), ("msvcp100", "2010"), ("mfc100", "2010"),
        ("msvcr90", "2008"), ("msvcp90", "2008"),
        ("msvcr80", "2005"), ("msvcp80", "2005"),
    ];

    private static readonly string[] DirectXPrefixes = ["d3dx9_", "d3dx10_", "d3dx11_", "xinput1_3", "xaudio2_7", "x3daudio1_7", "d3dcompiler_43"];

    private static readonly Dictionary<string, string> DotNetFrameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.NETCore.App"] = ".NET Runtime",
        ["Microsoft.WindowsDesktop.App"] = ".NET Desktop Runtime",
        ["Microsoft.AspNetCore.App"] = "ASP.NET Core Runtime",
    };

    public static IReadOnlyList<Prerequisite> Analyze(IFileSystem files, IEnumerable<string> roots)
    {
        var allFiles = roots.SelectMany(r => files.EnumerateFiles(r, recursive: true)).ToList();
        var localNames = allFiles.Select(WinPath.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<Prerequisite>();

        var binaries = allFiles
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Take(MaxBinaries);
        var architectures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binary in binaries)
        {
            if (Read(files, binary) is not { } pe)
                continue;
            var name = WinPath.GetFileName(binary);
            var arch = pe.Architecture ?? "x64";
            architectures[binary] = arch;

            foreach (var import in pe.Imports.Where(i => !localNames.Contains(i)))
            {
                if (VcRuntime140.Contains(import))
                    found.Add(new Prerequisite(PrerequisiteKind.VcRuntime, $"Microsoft Visual C++ 2015-2022 Redistributable ({arch})", $"{name} imports {import}")
                        { Architecture = arch });
                else if (VcRuntimeLegacy.FirstOrDefault(v => import.StartsWith(v.Prefix, StringComparison.OrdinalIgnoreCase)) is { Year: not null } legacy)
                    found.Add(new Prerequisite(PrerequisiteKind.VcRuntimeLegacy, $"Microsoft Visual C++ {legacy.Year} Redistributable ({arch})",
                        $"{name} imports {import}") { Architecture = arch, Version = legacy.Year });
                else if (DirectXPrefixes.Any(p => import.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new Prerequisite(PrerequisiteKind.DirectX, "DirectX End-User Runtime (June 2010)", $"{name} imports {import}")
                        { Architecture = arch, Version = import });
            }

            // A managed .exe without runtimeconfig.json runs on the .NET Framework.
            if (pe.IsManaged && binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !files.FileExists(WinPath.ChangeExtension(binary, ".runtimeconfig.json")))
            {
                var v2 = pe.ClrVersion!.StartsWith("v2", StringComparison.OrdinalIgnoreCase);
                found.Add(new Prerequisite(PrerequisiteKind.NetFramework, v2 ? ".NET Framework 3.5" : ".NET Framework 4.x",
                    $"{name} is a .NET Framework application ({pe.ClrVersion})") { Version = v2 ? "v2.0.50727" : "v4.0.30319" });
            }
        }

        if (localNames.Contains("WebView2Loader.dll"))
            found.Add(new Prerequisite(PrerequisiteKind.WebView2, "Microsoft Edge WebView2 Runtime", "The application ships WebView2Loader.dll"));

        foreach (var config in allFiles.Where(f => f.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)))
            found.AddRange(ReadRuntimeConfig(files, config, architectures));

        return found
            .GroupBy(p => (p.Kind, p.Architecture, p.Version, p.Framework))
            .Select(g => g.First())
            .ToList();
    }

    private static PeInfo? Read(IFileSystem files, string path)
    {
        if (files.GetFileMeta(path) is not { Length: > 0 and <= MaxBinarySize })
            return null;
        try
        {
            using var stream = files.OpenRead(path);
            return PeReader.Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Framework-dependent .NET apps list their shared frameworks; self-contained ones list "includedFrameworks".</summary>
    private static IEnumerable<Prerequisite> ReadRuntimeConfig(IFileSystem files, string path, Dictionary<string, string> architectures)
    {
        JsonDocument document;
        try
        {
            using var stream = files.OpenRead(path);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var options))
                yield break;
            var frameworks = new List<JsonElement>();
            if (options.TryGetProperty("framework", out var single))
                frameworks.Add(single);
            if (options.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array)
                frameworks.AddRange(many.EnumerateArray());

            // "App.runtimeconfig.json" → architecture of the "App.exe" host.
            var host = path[..^".runtimeconfig.json".Length] + ".exe";
            var arch = architectures.GetValueOrDefault(host, "x64");
            foreach (var framework in frameworks)
            {
                var name = framework.TryGetProperty("name", out var n) ? n.GetString() : null;
                var version = framework.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (name is null || version is null)
                    continue;
                var majorMinor = string.Join('.', version.Split('.').Take(2));
                yield return new Prerequisite(PrerequisiteKind.DotNet,
                    $"{DotNetFrameworkNames.GetValueOrDefault(name, name)} {majorMinor} ({arch})",
                    $"{WinPath.GetFileName(path)} requires {name} {version}")
                {
                    Architecture = arch, Version = majorMinor, Framework = name,
                };
            }
        }
    }
}
