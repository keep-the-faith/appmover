using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AppMover.Core.Models;
using AppMover.Core.Registry;
using AppMover.Packaging;
using AppMover.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static AppMover.Tests.Fakes.TestMachine;

namespace AppMover.Tests;

public sealed class PackagingTests : IDisposable
{
    private readonly TestMachine _machine = new TestMachine().WithAcmeEditor();
    private readonly string _output = Path.Combine(AppContext.BaseDirectory, "test-output", $"{Guid.NewGuid():N}{PackageManifest.Extension}");

    public PackagingTests() => Directory.CreateDirectory(Path.GetDirectoryName(_output)!);

    public void Dispose()
    {
        if (File.Exists(_output))
            File.Delete(_output);
    }

    private PackageResult Write(Func<IReadOnlyList<CollectedItem>, IReadOnlyList<CollectedItem>>? select = null)
    {
        using var session = _machine.OpenSession();
        var collection = _machine.CollectionService().Collect(_machine.App, session);
        var items = select?.Invoke(collection.Items) ?? collection.Items;
        return new PackageWriter(_machine.Registry, _machine.Files, NullLogger<PackageWriter>.Instance)
            .Write(_output, _machine.App, collection, items, session);
    }

    private static T Read<T>(ZipArchive zip, string entry)
    {
        using var stream = zip.GetEntry(entry)!.Open();
        return JsonSerializer.Deserialize<T>(stream, PackageManifest.JsonOptions)!;
    }

    [Fact]
    public void WritesSelectedFilesWithTokenizedPaths()
    {
        var result = Write();

        Assert.False(result.HasErrors);
        Assert.False(File.Exists(_output + ".partial"));
        using var zip = ZipFile.OpenRead(_output);
        var entries = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("files/InstallDir/editor.exe", entries);
        Assert.Contains("files/InstallDir/plugins/a.dll", entries);
        Assert.Contains("files/U0.AppData/Acme/Editor/settings.ini", entries);
        Assert.Contains("files/ProgramData/Acme Editor/license.dat", entries);
        Assert.Contains("files/CommonStartMenu/Programs/Acme Editor/Acme Editor.lnk", entries);
        Assert.Contains("files/System32/Tasks/Acme/Update", entries);
        // Possible items are not selected by default.
        Assert.DoesNotContain(entries, e => e.Contains("EditorCache"));
        Assert.DoesNotContain(entries, e => e.Contains("Documents"));
        Assert.Contains(PackageManifest.FileName, entries);
        Assert.Contains(PackageManifest.RegistryFileName, entries);
    }

    [Fact]
    public void ManifestDescribesFilesAndFolders()
    {
        Write();

        using var zip = ZipFile.OpenRead(_output);
        var manifest = Read<PackageManifest>(zip, PackageManifest.FileName);
        Assert.Equal(PackageManifest.CurrentFormatVersion, manifest.FormatVersion);
        Assert.Equal(InstallDir, manifest.InstallDir);
        Assert.Equal(InstallDir, manifest.Folders["InstallDir"]);
        Assert.Equal(@"C:\Users\bob\AppData\Roaming", manifest.Folders["U0.AppData"]);
        Assert.Equal(Bob, Assert.Single(manifest.Users).Sid);
        Assert.Equal("Acme Editor 2.1 (x64)", manifest.App.DisplayName);

        var editor = Assert.Single(manifest.Files, f => f.Path == @"{InstallDir}\editor.exe");
        Assert.Equal("files/InstallDir/editor.exe", editor.Entry);
        Assert.Equal(6, editor.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("EDITOR"u8)).ToLowerInvariant(), editor.Sha256);
        Assert.Equal(FakeFileSystem.LastWrite, editor.LastWriteUtc);

        Assert.Contains(@"{InstallDir}\empty", manifest.Directories);
        Assert.Equal("1.5.0.0", manifest.Files.Single(f => f.Path == @"{InstallDir}\plugins\engine.dll").FileVersion);
        Assert.Null(editor.FileVersion);
        Assert.Equal(PrerequisiteKind.VcRuntime, Assert.Single(manifest.Prerequisites).Kind);
        Assert.Equal(@"{InstallDir}\bin", Assert.Single(manifest.PathEntries).Entry);
        // Unselected items are kept for traceability.
        Assert.Contains(manifest.Items, i => !i.Selected);
    }

    [Fact]
    public void ExportsRegistryWithTokens()
    {
        Write();

        using var zip = ZipFile.OpenRead(_output);
        var records = Read<List<RegKeyRecord>>(zip, PackageManifest.RegistryFileName);

        var uninstall = Assert.Single(records, r => r.Path == $@"{Uninstall}\AcmeEditor");
        Assert.False(uninstall.Partial);
        Assert.Equal("{InstallDir}", uninstall.Values.Single(v => v.Name == "InstallLocation").Text);
        Assert.Equal(1234, uninstall.Values.Single(v => v.Name == "EstimatedSize").Number);
        Assert.Contains("InstallLocation", uninstall.TokenizedValues!);

        var recent = Assert.Single(records, r => r.Path == @"SOFTWARE\Acme\Editor\Recent");
        Assert.Equal([@"{InstallDir}\samples\a.acme", @"D:\b.acme"], recent.Values.Single().Multi!);

        var user = Assert.Single(records, r => r.Hive == RegHive.Users && r.Path == @"Software\Acme\Editor");
        Assert.Equal(0, user.UserIndex);

        var run = Assert.Single(records, r => r.Path.EndsWith(@"CurrentVersion\Run"));
        Assert.True(run.Partial);
        Assert.Equal(RegView.Registry32, run.View);
        Assert.Equal("AcmeTray", Assert.Single(run.Values).Name);

        Assert.Contains(records, r => r.Path == @"SYSTEM\CurrentControlSet\Services\AcmeSvc");
        // The PATH variable itself is never exported, only its entry (manifest).
        var environment = Assert.Single(records, r => r.Path.EndsWith(@"Session Manager\Environment"));
        Assert.True(environment.Partial);
        Assert.Equal("ACME_HOME", Assert.Single(environment.Values).Name);
    }

    [Fact]
    public void LockedFileIsReportedAndSkipped()
    {
        _machine.Files.Lock($@"{InstallDir}\plugins\a.dll");

        var result = Write();

        Assert.True(result.HasErrors);
        Assert.Contains(result.Manifest.Issues, i => i.Severity == IssueSeverity.Error && i.Location!.EndsWith("a.dll"));
        Assert.DoesNotContain(result.Manifest.Files, f => f.Path.EndsWith("a.dll"));
        Assert.Contains(result.Manifest.Files, f => f.Path.EndsWith("editor.exe"));
    }

    [Fact]
    public void HonoursSelection()
    {
        var result = Write(items => items.Select(i => i with { Selected = i.Kind == ItemKind.RegistryKey }).ToList());

        Assert.Empty(result.Manifest.Files);
        Assert.True(result.RegistryKeyCount > 0);
    }
}
