using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Text;

namespace AppMover.Deployment;

/// <summary>Checks whether the runtimes needed by an application are installed on this machine.</summary>
public sealed class PrerequisiteChecker(IRegistrySource registry, IFileSystem files, MachineFolders machine)
{
    private const string WebView2ClientKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly Dictionary<string, string> LegacyVcDlls = new()
    {
        ["2013"] = "msvcr120.dll", ["2012"] = "msvcr110.dll", ["2010"] = "msvcr100.dll",
    };

    /// <summary>True / false, or null when it cannot be checked (e.g. side-by-side Visual C++ 2005/2008).</summary>
    public bool? IsInstalled(Prerequisite prerequisite) => prerequisite.Kind switch
    {
        PrerequisiteKind.VcRuntime => SystemFileExists("vcruntime140.dll", prerequisite.Architecture),
        PrerequisiteKind.VcRuntimeLegacy => LegacyVcDlls.TryGetValue(prerequisite.Version ?? "", out var dll)
            ? SystemFileExists(dll, prerequisite.Architecture)
            : null,
        PrerequisiteKind.DirectX => prerequisite.Version is { } file ? SystemFileExists(file, prerequisite.Architecture) : null,
        PrerequisiteKind.NetFramework => prerequisite.Version?.StartsWith("v2", StringComparison.OrdinalIgnoreCase) == true
            ? NumberValue(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5", "Install") == 1
            : NumberValue(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release") is not null,
        PrerequisiteKind.DotNet => DotNetInstalled(prerequisite),
        PrerequisiteKind.WebView2 => WebView2Installed(),
        _ => null,
    };

    /// <summary>32-bit DLLs live in SysWOW64 on 64-bit Windows.</summary>
    private bool SystemFileExists(string fileName, string? architecture) =>
        files.FileExists(WinPath.Combine(architecture == "x86" ? machine.SysWow64 : machine.System32, fileName));

    /// <summary>%ProgramFiles%\dotnet\shared\&lt;framework&gt;\&lt;major.minor&gt;.x</summary>
    private bool? DotNetInstalled(Prerequisite prerequisite)
    {
        if (prerequisite.Framework is null || prerequisite.Version is null)
            return null;
        var root = prerequisite.Architecture == "x86" ? machine.ProgramFilesX86 : machine.ProgramFiles;
        var shared = WinPath.Combine(root, $@"dotnet\shared\{prerequisite.Framework}");
        return files.EnumerateDirectories(shared)
            .Any(d => WinPath.GetFileName(d).StartsWith(prerequisite.Version + ".", StringComparison.OrdinalIgnoreCase));
    }

    private bool WebView2Installed()
    {
        foreach (var view in new[] { RegView.Registry32, RegView.Registry64 })
        {
            using var key = registry.OpenKey(RegHive.LocalMachine, view, WebView2ClientKey);
            if (key?.GetString("pv") is { Length: > 0 } version && version != "0.0.0.0")
                return true;
        }
        return false;
    }

    private long? NumberValue(string path, string name)
    {
        using var key = registry.OpenKey(RegHive.LocalMachine, RegView.Registry64, path);
        return key?.GetValue(name)?.AsNumber();
    }
}
