namespace AppMover.Core.Platform;

/// <summary>Minimal read-only file access used by detection (mockable for tests).</summary>
public interface IFileProbe
{
    bool FileExists(string path);

    /// <summary>
    /// Searches the first <paramref name="maxBytes"/> bytes of a file for the markers (ASCII and UTF-16LE).
    /// Returns the markers found (empty if none or if the file cannot be read).
    /// </summary>
    IReadOnlySet<string> FindMarkers(string path, IReadOnlyCollection<string> markers, long maxBytes);
}
