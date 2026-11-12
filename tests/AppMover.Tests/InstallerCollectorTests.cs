using AppMover.Collectors;
using AppMover.Core.Inno;
using AppMover.Core.Models;
using AppMover.Core.Msi;
using AppMover.Core.Registry;
using AppMover.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static AppMover.Core.Inno.InnoUninstallLog;

namespace AppMover.Tests;

public class MsiKeyPathTests
{
    [Fact]
    public void File()
    {
        var keyPath = MsiKeyPath.Parse(@"C:\Program Files\App\app.exe");
        Assert.Equal(MsiKeyPathKind.File, keyPath.Kind);
        Assert.Equal(@"C:\Program Files\App\app.exe", keyPath.Path);
    }

    [Fact]
    public void Directory() => Assert.Equal(new MsiKeyPath(MsiKeyPathKind.Directory, @"C:\App\Data"), MsiKeyPath.Parse(@"C:\App\Data\"));

    [Theory]
    [InlineData(@"02:\SOFTWARE\Acme\Editor\Version", 2, false, @"SOFTWARE\Acme\Editor", "Version")]
    [InlineData(@"22:\SOFTWARE\Acme\Editor\Version", 2, true, @"SOFTWARE\Acme\Editor", "Version")]
    [InlineData(@"01:\Software\Acme\", 1, false, @"Software\Acme", "")]
    [InlineData(@"00:\.acme\", 0, false, ".acme", "")]
    public void Registry(string raw, int root, bool is64, string key, string value) =>
        Assert.Equal(new MsiKeyPath(MsiKeyPathKind.Registry, key, root, is64, value), MsiKeyPath.Parse(raw));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"C?\Program Files\App\app.exe")]
    public void None(string? raw) => Assert.Equal(MsiKeyPathKind.None, MsiKeyPath.Parse(raw).Kind);
}

public class MsiCollectorTests
{
    private const string ProductCode = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";
    private const string OtherProduct = "{99999999-9999-9999-9999-999999999999}";
    private const string LocalPackage = @"C:\Windows\Installer\1a2b3c.msi";
    private const string EditorDir = @"C:\Program Files\Acme\Editor";

    private readonly TestMachine _machine = new();
    private readonly CollectionResult _result;

    public MsiCollectorTests()
    {
        var userData = $@"{MsiRegistry.UserDataPath}\S-1-5-18";
        var product = MsiGuid.Pack(ProductCode)!;
        _machine.Registry.Set(RegHive.LocalMachine, $@"{userData}\Products\{product}\InstallProperties", RegValue.String("LocalPackage", LocalPackage));

        void Component(string guid, string keyPath, bool shared = false)
        {
            var values = new List<RegValue> { RegValue.String(product, keyPath) };
            if (shared)
                values.Add(RegValue.String(MsiGuid.Pack(OtherProduct)!, keyPath));
            _machine.Registry.Set(RegHive.LocalMachine, $@"{userData}\Components\{MsiGuid.Pack(guid)}", values.ToArray());
        }
        Component("{00000000-0000-0000-0000-000000000001}", $@"{EditorDir}\editor.exe");
        Component("{00000000-0000-0000-0000-000000000002}", $@"{EditorDir}\plugins\a.dll");
        Component("{00000000-0000-0000-0000-000000000003}", @"C:\Windows\System32\acmehelper.dll");
        Component("{00000000-0000-0000-0000-000000000004}", @"C:\Program Files\Common Files\Acme\shared.dll", shared: true);
        Component("{00000000-0000-0000-0000-000000000005}", @"22:\SOFTWARE\Acme\Editor\Version");
        Component("{00000000-0000-0000-0000-000000000006}", @"02:\SOFTWARE\Acme\Editor32\Installed");

        _machine.Files
            .Add(LocalPackage)
            .Add($@"{EditorDir}\editor.exe").Add($@"{EditorDir}\plugins\a.dll").Add($@"{EditorDir}\readme.txt")
            .Add(@"C:\Windows\System32\acmehelper.dll").Add(@"C:\Program Files\Common Files\Acme\shared.dll");

        var database = new FakeMsiDatabase()
            .Table("Component",
                ["CompMain", "{00000000-0000-0000-0000-000000000001}", "256"],
                ["Comp32", "{00000000-0000-0000-0000-000000000006}", "0"],
                ["CompNotInstalled", "{00000000-0000-0000-0000-000000000009}", "256"])
            .Table("Property", ["Manufacturer", "Acme"], ["ProductName", "Acme Editor"])
            .Table("Registry",
                ["2", @"Software\[Manufacturer]\Editor", "Theme", "dark", "CompMain"],
                ["2", @"Software\[Manufacturer]\Editor\Cache", "-", null, "CompMain"],
                ["2", @"Software\[Manufacturer]\Editor\Ensure", "+", null, "CompMain"],
                ["2", @"Software\[Unknown]\X", "A", "1", "CompMain"],
                ["2", @"Software\Acme\Ghost", "A", "1", "CompNotInstalled"],
                ["-1", @"Software\[Manufacturer]\Legacy", "Path", "[INSTALLDIR]", "Comp32"],
                ["0", @"AcmeFile\shell\open\command", null, "\"[#editor.exe]\" \"%1\"", "CompMain"]);

        var app = new InstalledApp
        {
            Id = $@"HKLM64\{ProductCode}", KeyName = ProductCode, RegistryPath = ProductCode, Scope = AppScope.Machine, View = RegView.Registry64,
            DisplayName = "Acme Editor", Publisher = "Acme", WindowsInstaller = true, RawValues = new Dictionary<string, RegValue>(),
            Installer = new InstallerInfo
            {
                Type = InstallerType.Msi, Confidence = 100, Evidence = [],
                Properties = new Dictionary<string, string> { [InstallerProperties.ProductCode] = ProductCode },
            },
        };

        using var session = _machine.OpenSession();
        _result = new CollectionService(_machine.Registry, _machine.Files, _machine.Paths, NullLogger<CollectionService>.Instance, database)
            .Collect(app, session);
    }

    private CollectedItem Find(string location) =>
        Assert.Single(_result.Items, i => i.Location.Equals(location, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void InstallFolderComesFromTheComponents()
    {
        var root = Assert.Single(_result.InstallRoots);
        Assert.Equal(EditorDir, root.Path);
        Assert.Contains("Windows Installer", root.Reason);
    }

    [Fact]
    public void FilesOutsideTheInstallFolder()
    {
        var system = Find(@"C:\Windows\System32\acmehelper.dll");
        Assert.Equal(Confidence.Possible, system.Confidence);
        Assert.NotNull(system.Warning);

        var shared = Find(@"C:\Program Files\Common Files\Acme\shared.dll");
        Assert.Equal(Confidence.Possible, shared.Confidence);
        Assert.Contains("shared", shared.Warning);

        Assert.DoesNotContain(_result.Items, i => i.Location.EndsWith(@"plugins\a.dll"));
    }

    [Fact]
    public void RegistryKeyPaths()
    {
        Assert.Equal(Confidence.Certain, Find(@"HKLM64\SOFTWARE\Acme\Editor → Version").Confidence);
        Assert.Equal(Confidence.Certain, Find(@"HKLM32\SOFTWARE\Acme\Editor32 → Installed").Confidence);
    }

    [Fact]
    public void RegistryTable()
    {
        Assert.Equal(Confidence.Certain, Find(@"HKLM64\Software\Acme\Editor → Theme").Confidence);
        Assert.Equal(ItemKind.RegistryKey, Find(@"HKLM64\Software\Acme\Editor\Cache").Kind);
        Assert.Equal(Confidence.Certain, Find(@"HKLM32\Software\Acme\Legacy → Path").Confidence);
        Assert.Equal(Confidence.Certain, Find(@"HKLM64\SOFTWARE\Classes\AcmeFile\shell\open\command → (Default)").Confidence);
        Assert.DoesNotContain(_result.Items, i => i.Location.Contains("Ensure") || i.Location.Contains("Unknown") || i.Location.Contains("Ghost"));
    }
}

public class InnoTests
{
    private const string InstallDir = @"C:\Program Files\Acme\Inno";
    private const string LogPath = $@"{InstallDir}\unins000.dat";
    private const uint Hklm = 0x80000002, Hkcu = 0x80000001, Hkcr = 0x80000000;

    private static InnoLogBuilder SampleLog() => new InnoLogBuilder()
        .Add(DeleteFile, 0, $@"{InstallDir}\acme.exe")
        .Add(DeleteFile, DeleteFileSharedFile, @"C:\Windows\System32\acmeinno.dll")
        .Add(DeleteFile, 0, @"C:\Users\Public\Documents\Acme\readme.txt")
        .Add(DeleteDirOrFiles, DeleteDirIsDir, @"C:\ProgramData\AcmeInnoData")
        .Add(DeleteDirOrFiles, DeleteDirExtra, @"C:\Users\bob\AppData\Roaming\AcmeLogs\*.log")
        .Add(DeleteGroupOrItem, 0, @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Inno")
        .Add(RegDeleteEntireKey, Hklm | Reg64BitKey, @"Software\AcmeInno")
        .Add(RegDeleteKeyIfEmpty, Hklm, @"Software\AcmeVendor")
        .Add(RegDeleteValue, Hkcu, @"Software\Microsoft\Windows\CurrentVersion\Run", "AcmeInno")
        .Add(RegDeleteValue, Hkcr, ".acmi", "")
        .Add(0x80, 0, "run.exe", "/uninstall"); // utRun: ignored

    [Fact]
    public void ParsesTheLog()
    {
        var log = InnoUninstallLog.Read(new MemoryStream(SampleLog().Build()));

        Assert.NotNull(log);
        Assert.Equal("AcmeInno_is1", log.AppId);
        Assert.Equal("Acme Inno", log.AppName);
        Assert.True(log.Is64BitMode);
        Assert.True(log.IsAdminInstall);
        Assert.Equal(11, log.Records.Count);
        Assert.Equal([@"Software\Microsoft\Windows\CurrentVersion\Run", "AcmeInno"], log.Records[8].Data);
        Assert.Equal(Hkcu, log.Records[8].ExtraData);
    }

    [Fact]
    public void RecordsCanSpanSeveralBlocks()
    {
        var builder = new InnoLogBuilder();
        for (var i = 0; i < 300; i++)
            builder.Add(DeleteFile, 0, $@"C:\Program Files\Acme\Inno\file{i:D4}.dat");

        var log = InnoUninstallLog.Read(new MemoryStream(builder.Build()));

        Assert.Equal(300, log!.Records.Count);
        Assert.Equal(@"C:\Program Files\Acme\Inno\file0299.dat", log.Records[^1].Data[0]);
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        Assert.Null(InnoUninstallLog.Read(new MemoryStream(new byte[1000])));
        Assert.Null(InnoUninstallLog.Read(new MemoryStream(new byte[10])));
        var corrupted = SampleLog().Build();
        corrupted[452] ^= 0xFF; // ~Size of the first block
        Assert.Null(InnoUninstallLog.Read(new MemoryStream(corrupted)));
    }

    [Fact]
    public void CollectsWhatTheUninstallerKnows()
    {
        var machine = new TestMachine();
        machine.Files
            .Add($@"{InstallDir}\acme.exe")
            .Add(LogPath, SampleLog().Build())
            .Add(@"C:\Windows\System32\acmeinno.dll")
            .Add(@"C:\Users\Public\Documents\Acme\readme.txt")
            .Add(@"C:\ProgramData\AcmeInnoData\db.sqlite")
            .Add(@"C:\Users\bob\AppData\Roaming\AcmeLogs\today.log")
            .Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Inno\Acme Inno.lnk");
        var app = new InstalledApp
        {
            Id = @"HKLM64\AcmeInno_is1", KeyName = "AcmeInno_is1", RegistryPath = "AcmeInno_is1", Scope = AppScope.Machine,
            View = RegView.Registry64, DisplayName = "Zeta Tool", InstallLocation = InstallDir, RawValues = new Dictionary<string, RegValue>(),
            Installer = new InstallerInfo
            {
                Type = InstallerType.InnoSetup, Confidence = 100, Evidence = [],
                Properties = new Dictionary<string, string> { [InstallerProperties.UninstallLog] = LogPath },
            },
        };

        using var session = machine.OpenSession();
        var items = machine.CollectionService().Collect(app, session).Items.Where(i => i.Collector == "Inno Setup").ToList();
        CollectedItem Find(string location) => Assert.Single(items, i => i.Location.Equals(location, StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(items, i => i.Location.EndsWith("acme.exe")); // inside the install folder
        Assert.Equal(Confidence.Possible, Find(@"C:\Windows\System32\acmeinno.dll").Confidence);
        Assert.Equal(Confidence.Certain, Find(@"C:\Users\Public\Documents\Acme\readme.txt").Confidence);
        Assert.Equal(Confidence.Certain, Find(@"C:\ProgramData\AcmeInnoData\").Confidence);
        var logs = Find(@"C:\Users\bob\AppData\Roaming\AcmeLogs\");
        Assert.Equal((ItemRole.Data, Confidence.Probable), (logs.Role, logs.Confidence));
        Assert.Equal(ItemRole.Shortcut, Find(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Inno\").Role);
        Assert.Equal(Confidence.Certain, Find(@"HKLM64\Software\AcmeInno").Confidence);
        Assert.Equal(Confidence.Possible, Find(@"HKLM32\Software\AcmeVendor").Confidence);
        Assert.Equal(Confidence.Certain, Find($@"HKU\{TestMachine.Bob}\Software\Microsoft\Windows\CurrentVersion\Run → AcmeInno").Confidence);
        Find(@"HKLM32\SOFTWARE\Classes\.acmi → (Default)");
        Assert.Equal(9, items.Count);
    }
}

/// <summary>A per-user WiX Burn bundle (like the Python installer): its files belong to the MSI packages it chains.</summary>
public class BurnBundleCollectorTests
{
    private const string BundleId = "{2E2E5545-FFDF-4F71-B419-3BAE183E81B7}";
    private const string CoreProduct = "{A0000000-0000-0000-0000-000000000001}";
    private const string LauncherProduct = "{A0000000-0000-0000-0000-000000000002}";
    private const string OtherPython = "{A0000000-0000-0000-0000-000000000003}";
    private const string Programs = @"C:\Users\bob\AppData\Local\Programs\Python";
    private const string BundleCache = $@"C:\Users\bob\AppData\Local\Package Cache\{BundleId}";

    private readonly TestMachine _machine = new();
    private readonly CollectionResult _result;

    public BurnBundleCollectorTests()
    {
        var userData = $@"{MsiRegistry.UserDataPath}\{TestMachine.Bob}";
        var dependencies = $@"{TestMachine.Bob}\Software\Classes\Installer\Dependencies";
        var componentCount = 0;

        void Product(string productCode, params string[] files)
        {
            var packed = MsiGuid.Pack(productCode)!;
            _machine.Registry.Set(RegHive.LocalMachine, $@"{userData}\Products\{packed}\InstallProperties");
            foreach (var file in files)
            {
                var component = $"{{B0000000-0000-0000-0000-{++componentCount:D12}}}";
                _machine.Registry.Set(RegHive.LocalMachine, $@"{userData}\Components\{MsiGuid.Pack(component)}", RegValue.String(packed, file));
                _machine.Files.Add(file);
            }
        }
        Product(CoreProduct, $@"{Programs}\Python314\python.exe", $@"{Programs}\Python314\python314.dll", $@"{Programs}\Python314\Lib\os.py");
        Product(LauncherProduct, $@"{Programs}\Launcher\py.exe");
        Product(OtherPython, $@"{Programs}\Python313\python.exe");

        // Provider named after the product, or holding the product code as its default value.
        _machine.Registry
            .Set(RegHive.Users, $@"{dependencies}\CPython-3.14-Core", RegValue.String("", CoreProduct))
            .Set(RegHive.Users, $@"{dependencies}\CPython-3.14-Core\Dependents\CPython-3.14")
            .Set(RegHive.Users, $@"{dependencies}\{LauncherProduct}\Dependents\{BundleId}")
            .Set(RegHive.Users, $@"{dependencies}\CPython-3.13-Core", RegValue.String("", OtherPython))
            .Set(RegHive.Users, $@"{dependencies}\CPython-3.13-Core\Dependents\CPython-3.13");
        _machine.Files.Add($@"{BundleCache}\python-3.14.10-amd64.exe");

        var app = new InstalledApp
        {
            Id = $@"HKU\{TestMachine.Bob}\{BundleId}", KeyName = BundleId, RegistryPath = BundleId, Scope = AppScope.User, UserSid = TestMachine.Bob, View = RegView.Default,
            DisplayName = "Python 3.14.10 (64-bit)", Publisher = "Python Software Foundation",
            DisplayIcon = $@"{BundleCache}\python-3.14.10-amd64.exe,0",
            RawValues = new Dictionary<string, RegValue> { ["BundleProviderKey"] = RegValue.String("BundleProviderKey", "CPython-3.14") },
            Installer = new InstallerInfo
            {
                Type = InstallerType.WixBurn, Confidence = 100, Evidence = [],
                Properties = new Dictionary<string, string> { [InstallerProperties.BundleCachePath] = $@"{BundleCache}\python-3.14.10-amd64.exe" },
            },
        };

        using var session = _machine.OpenSession();
        _result = new CollectionService(_machine.Registry, _machine.Files, _machine.Paths, NullLogger<CollectionService>.Instance).Collect(app, session);
    }

    [Fact]
    public void InstallFoldersComeFromTheChainedPackages()
    {
        Assert.Equal($@"{Programs}\Python314", _result.PrimaryInstallRoot);
        Assert.Equal([$@"{Programs}\Launcher", $@"{Programs}\Python314"], _result.InstallRoots.Select(r => r.Path).Order());
    }

    [Fact]
    public void PackagesOfOtherBundlesAreIgnored() =>
        Assert.DoesNotContain(_result.Items, i => i.Path?.Contains("Python313", StringComparison.OrdinalIgnoreCase) == true);

    [Fact]
    public void CachedBundleIsKeptForUninstallation()
    {
        var cache = Assert.Single(_result.Items, i => i.Path?.Equals(BundleCache, StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(ItemRole.UninstallEntry, cache.Role);
        Assert.True(cache.Selected);
    }
}
