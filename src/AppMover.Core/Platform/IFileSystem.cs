using System.Text;

namespace AppMover.Core.Platform;

/// <summary>Metadata of a file.</summary>
public sealed record FileMeta(long Length, DateTime LastWriteUtc, FileAttributes Attributes);

/// <summary>Read-only file system access (mockable for tests).</summary>
public interface IFileSystem : IFileProbe
{
    bool DirectoryExists(string path);

    /// <summary>Direct children directories. Reparse points (junctions, symlinks) are skipped; errors return nothing.</summary>
    IEnumerable<string> EnumerateDirectories(string path);

    /// <summary>Files of the directory (optionally recursive). Reparse points are skipped; inaccessible folders are ignored.</summary>
    IEnumerable<string> EnumerateFiles(string path, string pattern = "*", bool recursive = false);

    FileMeta? GetFileMeta(string path);

    Stream OpenRead(string path);
}

/// <summary>File system with write access (deployment).</summary>
public interface IWritableFileSystem : IFileSystem
{
    /// <summary>Creates the folder and its missing parents.</summary>
    void CreateDirectory(string path);

    /// <summary>Creates or truncates a file (read-only, hidden and system files are overwritten too).</summary>
    Stream Create(string path);

    /// <summary>Copies a file, replacing the destination.</summary>
    void Copy(string source, string destination);

    void DeleteFile(string path);

    /// <summary>Deletes a folder only when it is empty. Returns false otherwise.</summary>
    bool DeleteDirectory(string path);

    void SetMeta(string path, DateTime lastWriteUtc, FileAttributes attributes);

    /// <summary>Free space of the volume holding the path, or null when unknown.</summary>
    long? GetAvailableSpace(string path);
}

public sealed class PhysicalFileSystem : IWritableFileSystem
{
    private const int ChunkSize = 1024 * 1024;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        // Default skips Hidden|System: we want them all, except links that may point outside or loop.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path) =>
        Directory.Exists(path) ? Safe(() => Directory.EnumerateDirectories(path, "*", Options).ToList()) : [];

    public IEnumerable<string> EnumerateFiles(string path, string pattern = "*", bool recursive = false)
    {
        if (!Directory.Exists(path))
            yield break;

        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Safe(() => Directory.EnumerateFiles(dir, pattern, Options).ToList()))
                yield return file;
            if (recursive)
            {
                foreach (var sub in EnumerateDirectories(dir))
                    pending.Push(sub);
            }
        }
    }

    public FileMeta? GetFileMeta(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileMeta(info.Length, info.LastWriteTimeUtc, info.Attributes) : null;
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream Create(string path)
    {
        ClearProtection(path);
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
    }

    public void Copy(string source, string destination)
    {
        ClearProtection(destination);
        File.Copy(source, destination, overwrite: true);
    }

    public void DeleteFile(string path)
    {
        ClearProtection(path);
        File.Delete(path);
    }

    public bool DeleteDirectory(string path)
    {
        if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any())
            return false;
        Directory.Delete(path);
        return true;
    }

    public void SetMeta(string path, DateTime lastWriteUtc, FileAttributes attributes)
    {
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        File.SetAttributes(path, attributes);
    }

    public long? GetAvailableSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const FileAttributes Protective = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    /// <summary>
    /// Windows refuses to overwrite a read-only file, and to truncate a hidden or system one (desktop.ini…): these
    /// attributes are removed first. The caller sets the final attributes afterwards.
    /// </summary>
    private static void ClearProtection(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && (info.Attributes & Protective) != 0)
            info.Attributes &= ~Protective;
    }

    public IReadOnlySet<string> FindMarkers(string path, IReadOnlyCollection<string> markers, long maxBytes)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (markers.Count == 0 || !File.Exists(path))
            return found;

        var patterns = markers
            .SelectMany(m => new[] { (Marker: m, Bytes: Encoding.ASCII.GetBytes(m)), (Marker: m, Bytes: Encoding.Unicode.GetBytes(m)) })
            .ToList();
        var overlap = patterns.Max(p => p.Bytes.Length) - 1;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize);
            var buffer = new byte[ChunkSize + overlap];
            var kept = 0;
            long total = 0;
            while (total < maxBytes && found.Count < markers.Count)
            {
                var read = stream.Read(buffer, kept, (int)Math.Min(ChunkSize, maxBytes - total));
                if (read <= 0)
                    break;
                total += read;
                var span = buffer.AsSpan(0, kept + read);
                foreach (var (marker, bytes) in patterns)
                {
                    if (!found.Contains(marker) && span.IndexOf(bytes) >= 0)
                        found.Add(marker);
                }
                // Keep the tail so markers spanning two chunks are still found.
                kept = Math.Min(overlap, span.Length);
                span[^kept..].CopyTo(buffer);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable file: report what was found so far.
        }
        return found;
    }

    private static IEnumerable<string> Safe(Func<IEnumerable<string>> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
