namespace AppMover.Core.Models;

public enum PrerequisiteKind
{
    /// <summary>Visual C++ 2015-2022 Redistributable (vcruntime140.dll…).</summary>
    VcRuntime,
    /// <summary>Older Visual C++ Redistributable; <see cref="Prerequisite.Version"/> is the year (2013, 2012…).</summary>
    VcRuntimeLegacy,
    /// <summary>.NET Framework; <see cref="Prerequisite.Version"/> is the CLR version ("v4.0.30319", "v2.0.50727").</summary>
    NetFramework,
    /// <summary>.NET (Core) shared framework; <see cref="Prerequisite.Framework"/> + major.minor <see cref="Prerequisite.Version"/>.</summary>
    DotNet,
    /// <summary>Microsoft Edge WebView2 Runtime.</summary>
    WebView2,
    /// <summary>DirectX End-User Runtime (June 2010); <see cref="Prerequisite.Version"/> is the missing DLL.</summary>
    DirectX,
}

/// <summary>A runtime the application needs and that is not part of its files.</summary>
public sealed record Prerequisite(PrerequisiteKind Kind, string Name, string Reason)
{
    /// <summary>"x64", "x86", "arm64" when it matters.</summary>
    public string? Architecture { get; init; }
    public string? Version { get; init; }
    /// <summary>.NET shared framework name (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App…).</summary>
    public string? Framework { get; init; }
}
