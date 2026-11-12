// Reflection and resource checks only: they run on any OS although AppMover.Host targets Windows.
#pragma warning disable CA1416

using AppMover.Host;

namespace AppMover.Tests;

/// <summary>Usage recording in the single-file executable depends on TraceEvent internals: fail here, not on a user's PC.</summary>
public class KernelTraceControlLoaderTests
{
    [Fact]
    public void TraceEventLoaderFlagExists()
    {
        Assert.NotNull(KernelTraceControlLoader.LoadedFlag);
        Assert.Equal(typeof(bool), KernelTraceControlLoader.LoadedFlag!.FieldType);
    }

    [Fact]
    public void KernelTraceControlIsEmbedded()
    {
        using var resource = typeof(KernelTraceControlLoader).Assembly.GetManifestResourceStream("AppMover.Tools.KernelTraceControl.dll");
        Assert.NotNull(resource);
        Assert.True(resource!.Length > 0);
    }
}
