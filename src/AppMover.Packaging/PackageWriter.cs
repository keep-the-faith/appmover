using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Pe;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Shell;
using AppMover.Core.Text;
using Microsoft.Extensions.Logging;

namespace AppMover.Packaging;

public sealed record PackageResult(string Path, PackageManifest Manifest, long ArchiveSize)
{
    public int FileCount => Manifest.Files.Count;
    public long TotalSize => Manifest.Files.Sum(f => f.Size);
    public int RegistryKeyCount { get; init; }
    public bool HasErrors => Manifest.Issues.Any(i => i.Severity == IssueSeverity.Error);
}

/// <summary>Step 4: writes the selected items into a .appmove archive (ZIP).</summary>
public sealed class PackageWriter(IRegistrySource registry, IFileSystem files, ILogger<PackageWriter> logger)
{
    private static readonly DateTime MinZipDate = new(1980, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaxZipDate = new(2107, 12, 30, 0, 0, 0, DateTimeKind.Utc);

    /// <param name="outputPath">Archive to create (replaced if it exists).</param>
    /// <param name="app">The application.</param>
    /// <param name="collection">Result of the collection.</param>
    /// <param name="items">Items with their final selection (from the collection or a reviewed selection file).</param>
    /// <param name="session">The session used for the collection (users, mounted hives).</param>
    /// <param name="password">Encrypts the package (AES-256) when not null.</param>
    /// <param name="compression">ZIP compression level.</param>
    public PackageResult Write(string outputPath, InstalledApp app, CollectionResult collection, IReadOnlyList<CollectedItem> items,
        CollectionSession session, string? password = null, CompressionLevel compression = CompressionLevel.Optimal)
    {
        var tokenizer = session.CreateTokenizer(collection.PrimaryInstallRoot);
        var selected = items.Where(i => i.Selected).ToList();
        var issues = new List<PackageIssue>();
        foreach (var item in selected.Where(i => i.Warning is not null))
            issues.Add(new PackageIssue(IssueSeverity.Warning, item.Warning!, item.Location));

        var (sources, directories) = ListFiles(selected, outputPath, issues);
        var registryRecords = new RegistryExporter(registry, session, tokenizer).Export(selected, issues);
        var pathEntries = selected
            .Where(i => i.Kind == ItemKind.PathEntry)
            .Select(i => new PathEntryRecord(i.Registry!.Hive, session.FindUser(i.Registry.UserSid)?.Index, i.ValueName!, tokenizer.TokenizeText(i.Entry!)))
            .ToList();

        var manifest = new PackageManifest
        {
            ToolVersion = typeof(PackageWriter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            CreatedUtc = DateTime.UtcNow,
            Source = new SourceMachine(Environment.MachineName, RuntimeInformation.OSDescription, Environment.OSVersion.Version.ToString(),
                RuntimeInformation.OSArchitecture.ToString()),
            App = app,
            InstallDir = collection.PrimaryInstallRoot,
            Users = session.Users.Select(u => new PackageUser(u.Index, u.Sid, u.Folders.Profile, WinPath.GetFileName(u.Folders.Profile))).ToList(),
            Folders = tokenizer.Mappings.ToDictionary(m => m.Token, m => m.Path),
            Items = items.ToList(),
            Directories = directories.Select(tokenizer.Tokenize).ToList(),
            PathEntries = pathEntries,
            Shortcuts = ReadShortcuts(sources, tokenizer),
            Prerequisites = collection.Prerequisites.ToList(),
            Tasks = selected
                .Where(i => i.Role == ItemRole.ScheduledTask && i.Kind == ItemKind.File && i.Details?.ContainsKey("TaskName") == true)
                .Select(i => new TaskRecord(i.Details!["TaskName"], tokenizer.Tokenize(i.Path!)))
                .ToList(),
            Services = selected
                .Where(i => i.Role == ItemRole.Service && i.Details?.ContainsKey("ServiceName") == true)
                .Select(i => i.Details!["ServiceName"])
                .ToList(),
            Issues = issues,
        };

        var partialPath = outputPath + ".partial";
        try
        {
            using (var stream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var source in sources)
                {
                    if (WriteFile(zip, source, tokenizer, compression, issues) is { } file)
                        manifest.Files.Add(file);
                }
                WriteJson(zip, PackageManifest.RegistryFileName, registryRecords);
                WriteJson(zip, PackageManifest.FileName, manifest);
            }

            if (password is not null)
            {
                var encryptedPath = outputPath + ".encrypted.partial";
                using (var plain = File.OpenRead(partialPath))
                using (var encrypted = new FileStream(encryptedPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    PackageCrypto.Encrypt(plain, encrypted, password);
                File.Delete(partialPath);
                partialPath = encryptedPath;
            }
            File.Move(partialPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }

        logger.LogInformation("Package {Path}: {Files} file(s), {Keys} registry key(s), {Issues} issue(s)",
            outputPath, manifest.Files.Count, registryRecords.Count, issues.Count);
        return new PackageResult(outputPath, manifest, new FileInfo(outputPath).Length) { RegistryKeyCount = registryRecords.Count };
    }

    /// <summary>Files to archive and folders to recreate, without duplicates.</summary>
    private (List<string> Files, List<string> Directories) ListFiles(List<CollectedItem> selected, string outputPath, List<PackageIssue> issues)
    {
        var fileSet = new SortedSet<string>(WinPath.Comparer);
        var directorySet = new SortedSet<string>(WinPath.Comparer);
        var output = WinPath.Normalize(Path.GetFullPath(outputPath));

        foreach (var item in selected)
        {
            switch (item.Kind)
            {
                case ItemKind.Directory when files.DirectoryExists(item.Path!):
                    directorySet.Add(item.Path!);
                    foreach (var directory in AllDirectories(item.Path!))
                        directorySet.Add(WinPath.Normalize(directory));
                    foreach (var file in files.EnumerateFiles(item.Path!, recursive: true))
                        fileSet.Add(WinPath.Normalize(file));
                    break;
                case ItemKind.File when files.FileExists(item.Path!):
                    fileSet.Add(item.Path!);
                    break;
                case ItemKind.Directory or ItemKind.File:
                    issues.Add(new PackageIssue(IssueSeverity.Warning, "Not found", item.Path));
                    break;
            }
        }

        // Never archive the archive itself (e.g. exported into the application folder).
        fileSet.RemoveWhere(f => f.Equals(output, StringComparison.OrdinalIgnoreCase) || f.Equals(output + ".partial", StringComparison.OrdinalIgnoreCase));
        return (fileSet.ToList(), directorySet.ToList());
    }

    private List<ShortcutRecord> ReadShortcuts(IEnumerable<string> sources, PathTokenizer tokenizer)
    {
        var shortcuts = new List<ShortcutRecord>();
        foreach (var source in sources.Where(s => s.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
        {
            ShellLinkInfo? link;
            try
            {
                using var stream = files.OpenRead(source);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                link = ShellLink.Parse(memory.ToArray());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // reported when the file itself is archived
            }
            if (link is null)
                continue;

            string? Tokenize(string? text) => text is null ? null : tokenizer.TokenizeText(text);
            shortcuts.Add(new ShortcutRecord(tokenizer.Tokenize(source), Tokenize(link.Target), Tokenize(link.Arguments),
                Tokenize(link.WorkingDirectory), Tokenize(link.IconLocation), link.Description)
            {
                Advertised = link.DarwinId is not null,
            });
        }
        return shortcuts;
    }

    private IEnumerable<string> AllDirectories(string root)
    {
        foreach (var sub in files.EnumerateDirectories(root))
        {
            yield return sub;
            foreach (var nested in AllDirectories(sub))
                yield return nested;
        }
    }

    /// <summary>Configuration files whose content is checked for paths to adapt on the target.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini", ".cfg", ".conf", ".config", ".xml", ".json", ".jsonc", ".yaml", ".yml", ".properties", ".toml", ".settings", ".prefs",
        ".txt", ".cmd", ".bat", ".ps1", ".vbs", ".reg", ".inf", ".lst",
    };

    /// <summary>Text files larger than this are not inspected.</summary>
    private const long MaxTextFileSize = 4 * 1024 * 1024;

    private PackageFile? WriteFile(ZipArchive zip, string source, PathTokenizer tokenizer, CompressionLevel compression, List<PackageIssue> issues)
    {
        var tokenized = tokenizer.Tokenize(source);
        var meta = files.GetFileMeta(source);
        Stream input;
        try
        {
            input = files.OpenRead(source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            issues.Add(new PackageIssue(IssueSeverity.Error, $"Cannot read file: {e.Message}", source));
            return null;
        }

        var entryName = PathTokenizer.ToArchivePath(tokenized);
        using (input)
        {
            var entry = zip.CreateEntry(entryName, compression);
            var lastWrite = meta?.LastWriteUtc ?? DateTime.UtcNow;
            entry.LastWriteTime = lastWrite < MinZipDate ? MinZipDate : lastWrite > MaxZipDate ? MaxZipDate : lastWrite;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            var rewrite = false;
            try
            {
                using var output = entry.Open();
                if (IsTextCandidate(source, meta))
                {
                    // Small text file: read it whole to look for paths; the original bytes are archived.
                    using var memory = new MemoryStream();
                    input.CopyTo(memory);
                    var bytes = memory.ToArray();
                    rewrite = TextCodec.Decode(bytes) is { } text && tokenizer.TokenizeText(text.Text) != text.Text;
                    hash.AppendData(bytes);
                    output.Write(bytes);
                    size = bytes.Length;
                }
                else
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        output.Write(buffer, 0, read);
                        size += read;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The entry is incomplete: it is not listed in the manifest, so it will be ignored on deploy.
                issues.Add(new PackageIssue(IssueSeverity.Error, $"Read error: {e.Message}", source));
                return null;
            }

            Version? version = null;
            if (HasExtension(source, BinaryExtensions) && input.CanSeek)
            {
                input.Position = 0;
                version = PeReader.Read(input)?.FileVersion;
            }

            return new PackageFile
            {
                Path = tokenized,
                Entry = entryName,
                Size = size,
                Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                LastWriteUtc = lastWrite,
                Attributes = meta?.Attributes ?? FileAttributes.Normal,
                RewritePaths = rewrite,
                FileVersion = version?.ToString(),
            };
        }
    }

    /// <summary>Files whose version resource is recorded.</summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".ocx", ".cpl", ".drv", ".ax", ".scr", ".tlb", ".mui",
    };

    private static bool IsTextCandidate(string path, FileMeta? meta) =>
        meta is { Length: > 0 and <= MaxTextFileSize } && HasExtension(path, TextExtensions);

    private static bool HasExtension(string path, HashSet<string> extensions)
    {
        var name = WinPath.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot >= 0 && extensions.Contains(name[dot..]);
    }

    private static void WriteJson<T>(ZipArchive zip, string name, T value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, PackageManifest.JsonOptions);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
