using System.Text;
using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Text;

namespace AppMover.Tests.Fakes;

/// <summary>In-memory file system with Windows paths (case-insensitive).</summary>
public sealed class FakeFileSystem : IWritableFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileMeta> _meta = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DateTime LastWrite = new(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    public int ScanCount { get; private set; }

    public FakeFileSystem Add(string path, string content = "") => Add(path, Encoding.UTF8.GetBytes(content));

    public FakeFileSystem Add(string path, byte[] content)
    {
        _files[WinPath.Normalize(path)] = content;
        return this;
    }

    public FakeFileSystem AddDirectory(string path)
    {
        _directories.Add(WinPath.Normalize(path));
        return this;
    }

    /// <summary>Opening this file fails like a file locked by a running process.</summary>
    public FakeFileSystem Lock(string path)
    {
        _locked.Add(WinPath.Normalize(path));
        return this;
    }

    public bool FileExists(string path) => _files.ContainsKey(WinPath.Normalize(path));

    public bool DirectoryExists(string path)
    {
        var dir = WinPath.Normalize(path);
        return _directories.Contains(dir) || AllPaths().Any(p => IsStrictlyUnder(p, dir));
    }

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        var dir = WinPath.Normalize(path);
        return AllPaths()
            .Where(p => IsStrictlyUnder(p, dir))
            .Select(p => WinPath.Combine(dir, p[(dir.TrimEnd('\\').Length + 1)..].Split('\\')[0]))
            .Where(p => _directories.Contains(p) || AllPaths().Any(other => IsStrictlyUnder(other, p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IEnumerable<string> EnumerateFiles(string path, string pattern = "*", bool recursive = false)
    {
        var dir = WinPath.Normalize(path);
        var extension = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[1..] : null;
        return _files.Keys
            .Where(f => IsStrictlyUnder(f, dir) && (recursive || WinPath.GetDirectoryName(f)!.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            .Where(f => extension is null || f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public FileMeta? GetFileMeta(string path)
    {
        var normalized = WinPath.Normalize(path);
        if (!_files.TryGetValue(normalized, out var content))
            return null;
        return _meta.TryGetValue(normalized, out var meta) ? meta with { Length = content.Length } : new FileMeta(content.Length, LastWrite, FileAttributes.Archive);
    }

    public string ReadText(string path) => Encoding.UTF8.GetString(_files[WinPath.Normalize(path)]);

    public void CreateDirectory(string path)
    {
        // Like a real file system: folders that already exist (even implicitly, through their files) are left as they are.
        for (var dir = WinPath.Normalize(path); !WinPath.IsRoot(dir) && !DirectoryExists(dir); dir = WinPath.GetDirectoryName(dir)!)
            _directories.Add(dir);
    }

    public Stream Create(string path)
    {
        var normalized = WinPath.Normalize(path);
        if (!DirectoryExists(WinPath.GetDirectoryName(normalized)!) && !WinPath.IsRoot(WinPath.GetDirectoryName(normalized)!))
            throw new DirectoryNotFoundException(path);
        _meta.Remove(normalized);
        return new CapturingStream(bytes => _files[normalized] = bytes);
    }

    public void Copy(string source, string destination)
    {
        var bytes = _files[WinPath.Normalize(source)];
        _files[WinPath.Normalize(destination)] = bytes.ToArray();
    }

    public void DeleteFile(string path)
    {
        _files.Remove(WinPath.Normalize(path));
        _meta.Remove(WinPath.Normalize(path));
    }

    public bool DeleteDirectory(string path)
    {
        var dir = WinPath.Normalize(path);
        if (AllPaths().Any(p => IsStrictlyUnder(p, dir)))
            return false;
        return _directories.Remove(dir);
    }

    public void SetMeta(string path, DateTime lastWriteUtc, FileAttributes attributes) =>
        _meta[WinPath.Normalize(path)] = new FileMeta(0, lastWriteUtc, attributes);

    public long? GetAvailableSpace(string path) => AvailableSpace;

    public long? AvailableSpace { get; set; }

    /// <summary>Files (with their content) and folders, sorted, excluding those under <paramref name="excluded"/>.</summary>
    public List<string> Snapshot(string? excluded = null) =>
        _files.Select(f => $"{f.Key} = {Convert.ToHexString(f.Value)}")
            .Concat(_directories.Select(d => d + @""))
            .Where(p => excluded is null || !p.StartsWith(excluded, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Stores its content when disposed.</summary>
    private sealed class CapturingStream(Action<byte[]> onClose) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                onClose(ToArray());
            base.Dispose(disposing);
        }
    }

    public Stream OpenRead(string path)
    {
        var normalized = WinPath.Normalize(path);
        if (_locked.Contains(normalized))
            throw new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));
        return _files.TryGetValue(normalized, out var content) ? new MemoryStream(content, writable: false) : throw new FileNotFoundException(path);
    }

    public IReadOnlySet<string> FindMarkers(string path, IReadOnlyCollection<string> markers, long maxBytes)
    {
        ScanCount++;
        return _files.TryGetValue(WinPath.Normalize(path), out var content)
            ? markers.Where(m => Encoding.UTF8.GetString(content).Contains(m, StringComparison.Ordinal)).ToHashSet()
            : new HashSet<string>();
    }

    private IEnumerable<string> AllPaths() => _files.Keys.Concat(_directories);

    private static bool IsStrictlyUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

public static class StreamExtensions
{
    public static byte[] ReadAllBytesForTest(this Stream stream)
    {
        using (stream)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}

/// <summary>Expands a fixed set of variables.</summary>
public sealed class FakePaths : IPathResolver
{
    public string Expand(string path) => path
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%SystemDrive%", "C:", StringComparison.OrdinalIgnoreCase)
        .Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%LOCALAPPDATA%", @"C:\Users\bob\AppData\Local", StringComparison.OrdinalIgnoreCase);
}

public sealed class FakeHiveMounter : IHiveMounter
{
    public List<string> Mounted { get; } = [];
    public int Disposed { get; private set; }

    public IHiveMount? Mount(UserProfile profile)
    {
        Mounted.Add(profile.Sid);
        return new MountedHive("AppMover_" + profile.Sid, () => Disposed++);
    }

    private sealed class MountedHive(string keyName, Action onDispose) : IHiveMount
    {
        public string KeyName => keyName;
        public string ClassesKeyName => keyName + "_Classes";
        public void Dispose() => onDispose();
    }
}
