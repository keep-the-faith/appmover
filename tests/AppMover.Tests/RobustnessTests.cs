using System.Security.Cryptography;
using System.Text;
using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Text;
using AppMover.Deployment;
using AppMover.Packaging;
using AppMover.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static AppMover.Tests.Fakes.TestMachine;

namespace AppMover.Tests;

public class PathStyleTests
{
    private readonly PathTokenizer _tokenizer = new([("InstallDir", @"C:\Program Files\Acme"), ("U0.AppData", @"C:\Users\bob\AppData\Roaming")]);
    private readonly TokenMap _target = new(new Dictionary<string, string>
    {
        ["InstallDir"] = @"D:\Apps\Acme", ["U0.AppData"] = @"C:\Users\alice\AppData\Roaming",
    });

    [Theory]
    [InlineData(@"{""dir"": ""C:\\Program Files\\Acme\\bin""}", @"{""dir"": ""{InstallDir:json}\\bin""}", @"{""dir"": ""D:\\Apps\\Acme\\bin""}")]
    [InlineData("cache=C:/Users/bob/AppData/Roaming/Acme", "cache={U0.AppData:slash}/Acme", "cache=C:/Users/alice/AppData/Roaming/Acme")]
    [InlineData(@"<Path>C:\Program Files\Acme</Path>", "<Path>{InstallDir}</Path>", @"<Path>D:\Apps\Acme</Path>")]
    [InlineData(@"C:\Program Files\AcmeTools", @"C:\Program Files\AcmeTools", @"C:\Program Files\AcmeTools")]
    public void TokenizesAndExpandsEveryStyle(string text, string tokenized, string expanded)
    {
        Assert.Equal(tokenized, _tokenizer.TokenizeText(text));
        Assert.Equal(expanded, _target.ExpandText(tokenized));
    }

    [Fact]
    public void UnknownTokensAndGuidsAreLeftAlone() =>
        Assert.Equal("{U1.AppData:json} {11111111-2222-3333-4444-555555555555}",
            _target.ExpandText("{U1.AppData:json} {11111111-2222-3333-4444-555555555555}"));
}

public class TextCodecTests
{
    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("latin1")]
    public void RoundTripsEveryEncoding(string kind)
    {
        const string text = "Name=Éditeur\r\nPath=C:\\App";
        byte[] bytes = kind switch
        {
            "utf-8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
            "utf-8" => Encoding.UTF8.GetBytes(text),
            "utf-16" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            _ => Encoding.Latin1.GetBytes(text),
        };

        var decoded = TextCodec.Decode(bytes);

        Assert.NotNull(decoded);
        Assert.Equal(text, decoded.Text);
        Assert.Equal(bytes, TextCodec.Encode(decoded, decoded.Text));
        var changed = TextCodec.Encode(decoded, decoded.Text.Replace(@"C:\App", @"D:\App"));
        Assert.Equal(text.Replace(@"C:\App", @"D:\App"), TextCodec.Decode(changed)!.Text);
    }

    [Fact]
    public void BinaryDataIsNotText() => Assert.Null(TextCodec.Decode([0x4D, 0x5A, 0x00, 0x01]));
}

public class OfflineUserTests
{
    [Fact]
    public void ClassesOfAnOfflineUserComeFromUsrClassDat()
    {
        var machine = new TestMachine(loggedOn: false);
        machine.Registry
            .Set(RegHive.Users, $@"AppMover_{Bob}\Software\Acme\Editor", RegValue.String("Language", "fr"))
            .Set(RegHive.Users, $@"AppMover_{Bob}_Classes\acme", RegValue.String("URL Protocol", ""))
            .Set(RegHive.Users, $@"AppMover_{Bob}_Classes\acme\shell\open\command", RegValue.String("", $"\"{InstallDir}\\editor.exe\" \"%1\""));
        machine.Files.Add($@"{InstallDir}\editor.exe");

        using var session = machine.OpenSession();

        var user = Assert.Single(session.Users);
        Assert.Equal($"AppMover_{Bob}_Classes", user.ClassesHiveKey);
        Assert.Equal((RegHive.Users, RegView.Default, $@"AppMover_{Bob}_Classes\acme"),
            session.Resolve(RegistryLocation.User(Bob, @"Software\Classes\acme")));
        Assert.Equal((RegHive.Users, RegView.Default, $@"AppMover_{Bob}\Software\Acme"),
            session.Resolve(RegistryLocation.User(Bob, @"Software\Acme")));

        var items = machine.CollectionService().Collect(machine.App, session).Items;
        Assert.Contains(items, i => i.Location == $@"HKU\{Bob}\Software\Classes\acme" && i.Role == ItemRole.FileAssociation);
        Assert.Contains(items, i => i.Location == $@"HKU\{Bob}\Software\Acme\Editor");
    }
}

public class SnapshotTests
{
    private const string Locked = @"C:\Program Files\Acme\Editor\data.db";
    private const string ShadowRoot = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1";

    private sealed class FakeSnapshotProvider(bool fail = false) : ISnapshotProvider
    {
        public List<string> Created { get; } = [];
        public int Deleted { get; private set; }

        public IVolumeSnapshot Create(string volumeRoot)
        {
            if (fail)
                throw new IOException("VSS is not available");
            Created.Add(volumeRoot);
            return new Snapshot(() => Deleted++);
        }

        private sealed class Snapshot(Action onDispose) : IVolumeSnapshot
        {
            public string MapPath(string path) => $@"{ShadowRoot}\{path[3..]}";
            public void Dispose() => onDispose();
        }
    }

    private readonly FakeFileSystem _files = new FakeFileSystem()
        .Add(Locked, "DB").Lock(Locked)
        .Add(@"C:\Program Files\Acme\Editor\other.db", "OTHER").Lock(@"C:\Program Files\Acme\Editor\other.db")
        .Add($@"{ShadowRoot}\Program Files\Acme\Editor\data.db", "SNAPSHOT DB")
        .Add($@"{ShadowRoot}\Program Files\Acme\Editor\other.db", "SNAPSHOT OTHER");

    [Fact]
    public void LockedFilesAreReadFromOneSnapshotPerVolume()
    {
        var provider = new FakeSnapshotProvider();
        using (var files = new SnapshotFallbackFileSystem(_files, provider, NullLogger.Instance))
        {
            using (var reader = new StreamReader(files.OpenRead(Locked)))
                Assert.Equal("SNAPSHOT DB", reader.ReadToEnd());
            using (var reader = new StreamReader(files.OpenRead(@"C:\Program Files\Acme\Editor\other.db")))
                Assert.Equal("SNAPSHOT OTHER", reader.ReadToEnd());

            Assert.Equal(2, files.FilesFromSnapshot);
            Assert.Equal(["C:\\"], provider.Created);
        }
        Assert.Equal(1, provider.Deleted);
    }

    [Fact]
    public void WithoutSnapshotTheLockErrorIsKept()
    {
        using var files = new SnapshotFallbackFileSystem(_files, new FakeSnapshotProvider(fail: true), NullLogger.Instance);

        Assert.Throws<IOException>(() => files.OpenRead(Locked));
        Assert.Equal(0, files.FilesFromSnapshot);
    }
}

public class EncryptionTests
{
    [Fact]
    public void RoundTrip()
    {
        var data = RandomNumberGenerator.GetBytes(200_000);
        using var encrypted = new MemoryStream();
        PackageCrypto.Encrypt(new MemoryStream(data), encrypted, "correct horse", iterations: 1000);

        encrypted.Position = 0;
        Assert.True(PackageCrypto.IsEncrypted(encrypted));
        Assert.Equal(0, encrypted.Position);
        using var decrypted = new MemoryStream();
        PackageCrypto.Decrypt(encrypted, decrypted, "correct horse");
        Assert.Equal(data, decrypted.ToArray());
    }

    [Fact]
    public void WrongPasswordAndTamperingAreDetected()
    {
        using var encrypted = new MemoryStream();
        PackageCrypto.Encrypt(new MemoryStream(Encoding.UTF8.GetBytes("secret content")), encrypted, "correct horse", iterations: 1000);
        var bytes = encrypted.ToArray();

        Assert.Throws<CryptographicException>(() => PackageCrypto.Decrypt(new MemoryStream(bytes), new MemoryStream(), "wrong horse"));
        bytes[50] ^= 1;
        Assert.Throws<CryptographicException>(() => PackageCrypto.Decrypt(new MemoryStream(bytes), new MemoryStream(), "correct horse"));
        Assert.False(PackageCrypto.IsEncrypted(new MemoryStream(Encoding.UTF8.GetBytes("PK\x03\x04 zip"))));
    }
}

/// <summary>Export with snapshot + encryption, deploy on another machine: configuration files are adapted.</summary>
public sealed class RobustDeploymentTests : IDisposable
{
    private const string Alice = "S-1-5-21-9-9-9-1001";
    private const string TargetDir = @"D:\Programs\Acme\Editor";
    private const string Password = "a strong password";
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "test-output", $"{Guid.NewGuid():N}{PackageManifest.Extension}");
    private readonly PackageResult _result;

    public RobustDeploymentTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var source = new TestMachine().WithAcmeEditor();
        // The application is running: its database is locked but available in a snapshot.
        source.Files.Add($@"{InstallDir}\data.db", "LIVE").Lock($@"{InstallDir}\data.db")
            .Add($@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\Program Files\Acme\Editor\data.db", "LIVE");

        using var session = source.OpenSession();
        var collection = source.CollectionService().Collect(source.App, session);
        using var files = new SnapshotFallbackFileSystem(source.Files, new ShadowCopyOfC(), NullLogger.Instance);
        _result = new PackageWriter(source.Registry, files, NullLogger<PackageWriter>.Instance)
            .Write(_path, source.App, collection, collection.Items, session, Password);
    }

    private sealed class ShadowCopyOfC : ISnapshotProvider
    {
        public IVolumeSnapshot Create(string volumeRoot) => new Snapshot();

        private sealed class Snapshot : IVolumeSnapshot
        {
            public string MapPath(string path) => $@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\{path[3..]}";
            public void Dispose() { }
        }
    }

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void PackageIsEncryptedAndComplete()
    {
        Assert.False(_result.HasErrors);
        Assert.Contains(_result.Manifest.Files, f => f.Path == @"{InstallDir}\data.db");
        Assert.True(PackageCrypto.IsEncrypted(_path));
        Assert.Throws<PackageEncryptedException>(() => PackageReader.Open(_path));
        Assert.Throws<CryptographicException>(() => PackageReader.Open(_path, "not the password"));

        using var package = PackageReader.Open(_path, Password);
        Assert.Empty(package.Validate(verifyHashes: true));
        Assert.True(package.Manifest.Files.Single(f => f.Path == @"{InstallDir}\config.json").RewritePaths);
        Assert.True(package.Manifest.Files.Single(f => f.Path == @"{U0.AppData}\Acme\Editor\paths.ini").RewritePaths);
        Assert.False(package.Manifest.Files.Single(f => f.Path == @"{U0.AppData}\Acme\Editor\settings.ini").RewritePaths);
    }

    [Fact]
    public void ConfigurationFilesAndVirtualStoreFollowTheTarget()
    {
        var target = new TestMachine(Alice, "alice", @"D:\Programs");
        var system = new FakeSystem(target);
        using var package = PackageReader.Open(_path, Password);
        using var session = target.OpenSession();
        var plan = new DeploymentPlanner(target.Registry, target.Files, system.Value).Plan(package, session,
            new DeployOptions { UserMap = new Dictionary<int, string> { [0] = Alice }, UninstallCommand = "appmover uninstall" });

        new DeploymentExecutor(target.Registry, target.Registry, target.Files, system.Value,
            new DeploymentStore(target.Files, @"C:\ProgramData\AppMover\Deployments"), NullLogger<DeploymentExecutor>.Instance)
            .Deploy(plan, package, session);

        Assert.Equal("""{ "plugins": "D:\\Programs\\Acme\\Editor\\plugins", "cache": "C:/Users/alice/AppData/Local/EditorCache" }""",
            target.Files.ReadText($@"{TargetDir}\config.json"));
        Assert.Equal("[Paths]\r\nHome=D:\\Programs\\Acme\\Editor\r\nData=C:\\Users\\alice\\AppData\\Roaming\\Acme\\Editor\r\n",
            target.Files.ReadText(@"C:\Users\alice\AppData\Roaming\Acme\Editor\paths.ini"));
        Assert.Equal("theme=dark", target.Files.ReadText(@"C:\Users\alice\AppData\Roaming\Acme\Editor\settings.ini"));
        Assert.Equal("LIVE", target.Files.ReadText($@"{TargetDir}\data.db"));
        // The UAC VirtualStore mirrors the new install folder.
        Assert.True(target.Files.FileExists(@"C:\Users\alice\AppData\Local\VirtualStore\Programs\Acme\Editor\user.cfg"));
        Assert.False(target.Files.FileExists(@"C:\Users\alice\AppData\Local\VirtualStore\Program Files\Acme\Editor\user.cfg"));
    }
}
