using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;

namespace AppMover.Host;

/// <summary>
/// Makes KernelTraceControl.dll (needed by TraceEvent to start the kernel session) available to the single-file executable.
/// TraceEvent loads it from "&lt;folder of its assembly&gt;\%PROCESSOR_ARCHITECTURE%\", which does not exist in a single-file
/// application ("The specified module could not be found"). The embedded copy is extracted and checked like 7-Zip, loaded by
/// its full path, and TraceEvent's loader is marked as done: its [DllImport("KernelTraceControl.dll")] then binds to the module
/// already loaded under that name.
/// </summary>
internal static class KernelTraceControlLoader
{
    private const string ResourceName = "AppMover.Tools.KernelTraceControl.dll";

    /// <summary>Private flag of TraceEvent's loader (ETWKernelControl.LoadKernelTraceControl).</summary>
    internal static readonly FieldInfo? LoadedFlag =
        typeof(ETWKernelControl).GetField("s_KernelTraceControlLoaded", BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly object Gate = new();
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded)
                return;
            var flag = LoadedFlag ?? throw new MissingFieldException(
                "This version of TraceEvent no longer has ETWKernelControl.s_KernelTraceControlLoaded: update KernelTraceControlLoader.");
            // Same file name as the DllImport, so that the loaded module is found by name.
            NativeLibrary.Load(EmbeddedTools.Extract(ResourceName, hash => Path.Combine($"KernelTraceControl-{hash}", "KernelTraceControl.dll")));
            flag.SetValue(null, true);
            _loaded = true;
        }
    }
}
