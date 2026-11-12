using AppMover.Core.Text;
using Microsoft.Extensions.Logging;

namespace AppMover.Core.Platform;

/// <summary>A point-in-time copy of a volume (Volume Shadow Copy).</summary>
public interface IVolumeSnapshot : IDisposable
{
    /// <summary>Path of the same file inside the snapshot.</summary>
    string MapPath(string path);
}

public interface ISnapshotProvider
{
    /// <summary>Creates a snapshot of a volume ("C:\"). Throws when it is not possible.</summary>
    IVolumeSnapshot Create(string volumeRoot);
}

/// <summary>
/// File system that reads a file locked by a running program (sharing violation) from a snapshot of its volume.
/// The snapshot is only created when the first locked file of the volume is met, and deleted on dispose.
/// </summary>
public sealed class SnapshotFallbackFileSystem(IFileSystem inner, ISnapshotProvider provider, ILogger logger) : IFileSystem, IDisposable
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly Dictionary<string, IVolumeSnapshot?> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of files read from a snapshot.</summary>
    public int FilesFromSnapshot { get; private set; }

    public Stream OpenRead(string path)
    {
        try
        {
            return inner.OpenRead(path);
        }
        catch (IOException e) when ((e.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation)
        {
            if (Snapshot(path) is not { } snapshot)
                throw;
            var stream = inner.OpenRead(snapshot.MapPath(path));
            FilesFromSnapshot++;
            return stream;
        }
    }

    /// <summary>The snapshot of the volume of the path; null (and not retried) when it cannot be created.</summary>
    private IVolumeSnapshot? Snapshot(string path)
    {
        var normalized = WinPath.Normalize(path);
        if (normalized.Length < 3 || normalized[1] != ':')
            return null;
        var volume = normalized[..3];
        if (_snapshots.TryGetValue(volume, out var existing))
            return existing;

        IVolumeSnapshot? snapshot = null;
        try
        {
            logger.LogInformation("Locked file met on {Volume}: creating a volume snapshot", volume);
            snapshot = provider.Create(volume);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Cannot create a snapshot of {Volume}: locked files will be skipped", volume);
        }
        _snapshots[volume] = snapshot;
        return snapshot;
    }

    public void Dispose()
    {
        foreach (var snapshot in _snapshots.Values)
            snapshot?.Dispose();
        _snapshots.Clear();
    }

    public bool FileExists(string path) => inner.FileExists(path);
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);
    public IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
    public IEnumerable<string> EnumerateFiles(string path, string pattern = "*", bool recursive = false) => inner.EnumerateFiles(path, pattern, recursive);
    public FileMeta? GetFileMeta(string path) => inner.GetFileMeta(path);

    public IReadOnlySet<string> FindMarkers(string path, IReadOnlyCollection<string> markers, long maxBytes) =>
        inner.FindMarkers(path, markers, maxBytes);
}
