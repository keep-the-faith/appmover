using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AppMover.Packaging;

namespace AppMover.Deployment;

/// <summary>Reads and validates a .appmove package.</summary>
public sealed class PackageReader : IDisposable
{
    private readonly ZipArchive _zip;

    public PackageReader(Stream stream, string path)
    {
        Path = path;
        _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        Manifest = ReadJson<PackageManifest>(PackageManifest.FileName)
                   ?? throw new InvalidDataException($"{path} is not an AppMover package (no {PackageManifest.FileName}).");
        Registry = ReadJson<List<RegKeyRecord>>(PackageManifest.RegistryFileName) ?? [];
    }

    public string Path { get; }
    public PackageManifest Manifest { get; }
    public IReadOnlyList<RegKeyRecord> Registry { get; }

    /// <summary>Opens a package; an encrypted one is decrypted (and authenticated) into a temporary file deleted on dispose.</summary>
    /// <exception cref="PackageEncryptedException">The package is encrypted and no password was given.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Wrong password or corrupted package.</exception>
    public static PackageReader Open(string path, string? password = null)
    {
        Stream stream = File.OpenRead(path);
        try
        {
            if (PackageCrypto.IsEncrypted(stream))
            {
                if (password is null)
                    throw new PackageEncryptedException(path);
                var plain = new FileStream(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"appmover-{Guid.NewGuid():N}.tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
                using (stream)
                {
                    try
                    {
                        PackageCrypto.Decrypt(stream, plain, password);
                    }
                    catch
                    {
                        plain.Dispose();
                        throw;
                    }
                }
                plain.Position = 0;
                stream = plain;
            }
            return new PackageReader(stream, path);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public Stream OpenEntry(string name) =>
        (_zip.GetEntry(name) ?? throw new InvalidDataException($"Entry {name} is missing from the package.")).Open();

    /// <summary>Checks the structure of the package and, optionally, the SHA-256 of every file.</summary>
    public List<PackageIssue> Validate(bool verifyHashes)
    {
        var issues = new List<PackageIssue>();
        if (Manifest.FormatVersion > PackageManifest.CurrentFormatVersion)
            issues.Add(new PackageIssue(IssueSeverity.Error,
                $"Package format {Manifest.FormatVersion} is newer than this AppMover ({PackageManifest.CurrentFormatVersion}): update AppMover."));

        foreach (var path in Manifest.Directories.Concat(Manifest.Shortcuts.Select(s => s.Path)).Concat(Manifest.Tasks.Select(t => t.Path)))
        {
            if (!IsSafePath(path))
                issues.Add(new PackageIssue(IssueSeverity.Error, "Invalid path in the manifest", path));
        }

        foreach (var file in Manifest.Files)
        {
            if (!IsSafePath(file.Path))
            {
                issues.Add(new PackageIssue(IssueSeverity.Error, "Invalid path in the manifest", file.Path));
                continue;
            }
            var entry = _zip.GetEntry(file.Entry);
            if (entry is null)
                issues.Add(new PackageIssue(IssueSeverity.Error, "File missing from the package", file.Path));
            else if (entry.Length != file.Size)
                issues.Add(new PackageIssue(IssueSeverity.Error, $"Size mismatch ({entry.Length} instead of {file.Size})", file.Path));
            else if (verifyHashes && !Sha256(entry).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                issues.Add(new PackageIssue(IssueSeverity.Error, "Corrupted file (SHA-256 mismatch)", file.Path));
        }
        return issues;
    }

    /// <summary>
    /// A path of the manifest is either tokenized (<c>{Token}\…</c>) or absolute (<c>D:\…</c>, <c>\\server\share\…</c>),
    /// and never climbs with "..": a crafted package cannot write outside the expected folders.
    /// </summary>
    public static bool IsSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Split('\\', '/').Any(segment => segment.Trim() == ".."))
            return false;
        if (path.StartsWith('{'))
            return path.IndexOf('}') > 1;
        return (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
               || path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static string Sha256(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private T? ReadJson<T>(string name)
    {
        var entry = _zip.GetEntry(name);
        if (entry is null)
            return default;
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, PackageManifest.JsonOptions);
    }

    public void Dispose() => _zip.Dispose();
}
