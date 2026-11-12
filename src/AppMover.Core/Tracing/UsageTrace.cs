namespace AppMover.Core.Tracing;

/// <summary>What the processes of an application read and wrote while it was used (dynamic analysis).</summary>
public sealed record UsageTrace
{
    /// <summary>Id of the traced application (see InstalledApp.Id).</summary>
    public required string AppId { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DateTime StoppedUtc { get; init; }
    /// <summary>Command lines of the traced processes.</summary>
    public List<string> Processes { get; init; } = [];
    public List<string> FilesWritten { get; init; } = [];
    public List<string> FilesRead { get; init; } = [];
    /// <summary>Native names of the registry keys created or written (\REGISTRY\MACHINE\…, \REGISTRY\USER\&lt;SID&gt;\…).</summary>
    public List<string> RegistryKeysWritten { get; init; } = [];
}

/// <summary>Records the file and registry activity of an application's processes.</summary>
public interface IUsageTracer
{
    /// <param name="appId">Application traced.</param>
    /// <param name="installRoots">Processes whose command line is in these folders are traced, with their children.</param>
    /// <param name="launch">Executable to start once tracing (null: only attach to running and new processes).</param>
    IUsageTraceSession Start(string appId, IReadOnlyList<string> installRoots, string? launch);
}

public interface IUsageTraceSession : IDisposable
{
    /// <summary>Stops tracing and returns what was recorded.</summary>
    UsageTrace Stop();
}
