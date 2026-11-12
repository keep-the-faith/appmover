using AppMover.Core.Models;
using AppMover.Core.Msi;
using AppMover.Core.Registry;
using AppMover.Detection;
using AppMover.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppMover.Tests;

public class DetectionTests
{
    private readonly FakeRegistry _registry = new();
    private readonly FakeFileSystem _files = new();

    private InstallerInfo Detect(string keyName, params RegValue[] values)
    {
        var app = new InstalledApp
        {
            Id = @"HKLM64\" + keyName,
            KeyName = keyName,
            RegistryPath = keyName,
            Scope = AppScope.Machine,
            View = RegView.Registry64,
            UninstallString = values.FirstOrDefault(v => v.Name == "UninstallString")?.Text,
            WindowsInstaller = values.Any(v => v.Name == "WindowsInstaller" && v.Number == 1),
            RawValues = values.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase),
        };
        var detector = new InstallerDetector(new DetectionContext(_registry, _files, new FakePaths()), NullLogger<InstallerDetector>.Instance);
        return detector.Detect(app);
    }

    [Fact]
    public void Msi()
    {
        const string code = "{23170F69-40C1-2702-1900-000001000000}";
        _registry.Set(RegHive.LocalMachine, $@"{MsiRegistry.UserDataPath}\S-1-5-18\Products\{MsiGuid.Pack(code)}\InstallProperties",
            RegValue.String("LocalPackage", @"C:\Windows\Installer\1a2b.msi"));
        _files.Add(@"C:\Windows\Installer\1a2b.msi");

        var info = Detect(code, RegValue.DWord("WindowsInstaller", 1), RegValue.ExpandString("UninstallString", $"MsiExec.exe /X{code}"));

        Assert.Equal(InstallerType.Msi, info.Type);
        Assert.Equal(100, info.Confidence);
        Assert.Equal(code, info.Properties[InstallerProperties.ProductCode]);
        Assert.Equal("S-1-5-18", info.Properties[InstallerProperties.MsiUserDataSid]);
        Assert.Equal(@"C:\Windows\Installer\1a2b.msi", info.Properties[InstallerProperties.LocalPackage]);
        Assert.Equal(0, _files.ScanCount); // msiexec is never scanned
    }

    [Fact]
    public void MsiProductCodeFromCommandLineWhenKeyIsNotAGuid()
    {
        var info = Detect("MyApp", RegValue.String("UninstallString", "MsiExec.exe /I{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}"));

        Assert.Equal(InstallerType.Msi, info.Type);
        Assert.Equal("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}", info.Properties[InstallerProperties.ProductCode]);
    }

    [Fact]
    public void GuidKeyAloneIsNotMsi()
    {
        var info = Detect("{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}", RegValue.String("UninstallString", @"C:\App\remove.exe"));

        Assert.Equal(InstallerType.Unknown, info.Type);
        Assert.Contains(info.Evidence, e => e.Weight == 10);
    }

    [Fact]
    public void InnoSetup()
    {
        _files.Add(@"C:\Program Files\App\unins000.exe", "... Inno Setup ...").Add(@"C:\Program Files\App\unins000.dat");

        var info = Detect("App_is1",
            RegValue.String("UninstallString", "\"C:\\Program Files\\App\\unins000.exe\""),
            RegValue.String("Inno Setup: App Path", @"C:\Program Files\App"),
            RegValue.String("Inno Setup: Setup Version", "6.2.2"));

        Assert.Equal(InstallerType.InnoSetup, info.Type);
        Assert.Equal(100, info.Confidence);
        Assert.Equal(@"C:\Program Files\App\unins000.dat", info.Properties[InstallerProperties.UninstallLog]);
        Assert.Equal(@"C:\Program Files\App", info.Properties[InstallerProperties.InstallerAppPath]);
        Assert.Equal(@"C:\Program Files\App\unins000.exe", info.UninstallerPath);
    }

    [Fact]
    public void Nsis()
    {
        _files.Add(@"C:\Program Files\Notepad++\uninstall.exe", "MZ... Nullsoft.NSIS.exehead ... NullsoftInst ...");

        var info = Detect("Notepad++", RegValue.String("UninstallString", @"C:\Program Files\Notepad++\uninstall.exe"));

        Assert.Equal(InstallerType.Nsis, info.Type);
        Assert.Equal(80, info.Confidence);
    }

    [Fact]
    public void NsisNameAloneIsNotEnough()
    {
        _files.Add(@"C:\App\uninst.exe", "MZ");

        Assert.Equal(InstallerType.Unknown, Detect("App", RegValue.String("UninstallString", @"C:\App\uninst.exe")).Type);
    }

    [Fact]
    public void UninstallerIsScannedOnlyOnce()
    {
        _files.Add(@"C:\App\uninst.exe", "NullsoftInst");

        Detect("App", RegValue.String("UninstallString", @"C:\App\uninst.exe"));

        Assert.Equal(1, _files.ScanCount);
    }

    [Fact]
    public void InstallShieldWrappingMsiKeepsBoth()
    {
        const string code = "{11111111-2222-3333-4444-555555555555}";
        var info = Detect(code,
            RegValue.DWord("WindowsInstaller", 1),
            RegValue.String("UninstallString", $"MsiExec.exe /X{code}"),
            RegValue.String("LogFile", @"C:\Program Files (x86)\InstallShield Installation Information\{x}\setup.ilg"));

        Assert.Equal(InstallerType.Msi, info.Type);
        Assert.Contains(info.Secondary, s => s.Type == InstallerType.InstallShield);
    }

    [Fact]
    public void InstallShieldScript()
    {
        var info = Detect("{11111111-2222-3333-4444-555555555555}",
            RegValue.String("UninstallString",
                "\"C:\\Program Files (x86)\\InstallShield Installation Information\\{11111111-2222-3333-4444-555555555555}\\setup.exe\" -runfromtemp -l0x0409 -removeonly"));

        Assert.Equal(InstallerType.InstallShield, info.Type);
        Assert.Equal(90, info.Confidence);
    }

    [Fact]
    public void Squirrel()
    {
        _files.Add(@"C:\Users\bob\AppData\Local\Chat\Update.exe", "Squirrel");

        var info = Detect("Chat", RegValue.String("UninstallString", "\"%LOCALAPPDATA%\\Chat\\Update.exe\" --uninstall -s"));

        Assert.Equal(InstallerType.Squirrel, info.Type);
        Assert.Equal(90, info.Confidence);
        Assert.Equal(@"C:\Users\bob\AppData\Local\Chat\Update.exe", info.UninstallerPath);
    }

    [Fact]
    public void ClickOnceIsNotTransferable()
    {
        var info = Detect("abc", RegValue.String("UninstallString", "rundll32.exe dfshim.dll,ShArpMaintain Tool.application"));

        Assert.Equal(InstallerType.ClickOnce, info.Type);
        Assert.False(info.IsTransferable);
    }

    [Fact]
    public void WixBurnBundle()
    {
        var info = Detect("{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}",
            RegValue.String("BundleCachePath", @"C:\ProgramData\Package Cache\{x}\setup.exe"),
            RegValue.String("BundleProviderKey", "x"),
            RegValue.MultiString("BundleUpgradeCode", "{y}"),
            RegValue.String("UninstallString", "\"C:\\ProgramData\\Package Cache\\{x}\\setup.exe\" /uninstall"));

        Assert.Equal(InstallerType.WixBurn, info.Type);
        Assert.Equal(100, info.Confidence);
        Assert.Equal(@"C:\ProgramData\Package Cache\{x}\setup.exe", info.Properties[InstallerProperties.BundleCachePath]);
    }

    [Fact]
    public void Unknown()
    {
        var info = Detect("Tool", RegValue.String("UninstallString", @"C:\Tool\remove.exe"));

        Assert.Equal(InstallerType.Unknown, info.Type);
        Assert.Equal(0, info.Confidence);
        Assert.Equal(@"C:\Tool\remove.exe", info.UninstallerPath);
    }
}
