using AppMover.Core.Models;
using AppMover.Core.Registry;
using AppMover.Inventory;
using AppMover.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppMover.Tests;

public class InventoryTests
{
    private const string Uninstall = InventoryService.UninstallPath;
    private const string Bob = "S-1-5-21-1-2-3-1001";
    private const string Alice = "S-1-5-21-1-2-3-1002";

    private readonly FakeRegistry _registry = new();
    private readonly FakeHiveMounter _mounter = new();

    public InventoryTests()
    {
        _registry
            .Set(RegHive.LocalMachine, $@"{UserProfileProvider.ProfileListPath}\S-1-5-18", RegValue.ExpandString("ProfileImagePath", @"%SystemRoot%\system32\config\systemprofile"))
            .Set(RegHive.LocalMachine, $@"{UserProfileProvider.ProfileListPath}\{Bob}", RegValue.ExpandString("ProfileImagePath", @"%SystemDrive%\Users\bob"))
            .Set(RegHive.LocalMachine, $@"{UserProfileProvider.ProfileListPath}\{Alice}", RegValue.ExpandString("ProfileImagePath", @"%SystemDrive%\Users\alice"))
            .Set(RegHive.LocalMachine, $@"{UserProfileProvider.ProfileListPath}\{Alice}.bak", RegValue.ExpandString("ProfileImagePath", @"C:\Users\alice.old"))
            .Set(RegHive.Users, Bob)
            .Set(RegHive.Users, "S-1-5-18");
    }

    private InventoryService CreateService() =>
        new(_registry, new UserProfileProvider(_registry, new FakePaths()), _mounter, Bob, NullLogger<InventoryService>.Instance);

    private void AddApp(RegHive hive, RegView view, string root, string keyName, params RegValue[] values) =>
        _registry.Set(hive, view, $@"{root}\{keyName}", values);

    [Fact]
    public void ProfilesSkipSystemAndBackupEntries()
    {
        var profiles = new UserProfileProvider(_registry, new FakePaths()).GetProfiles();

        Assert.Equal([Bob, Alice], profiles.Select(p => p.Sid));
        Assert.Equal(@"C:\Users\bob", profiles[0].ProfilePath);
        Assert.True(profiles[0].IsLoaded);
        Assert.False(profiles[1].IsLoaded);
    }

    [Fact]
    public void ReadsMachine64Machine32AndCurrentUser()
    {
        AddApp(RegHive.LocalMachine, RegView.Registry64, Uninstall, "App64",
            RegValue.String("DisplayName", "App 64"), RegValue.String("UninstallString", "u.exe"), RegValue.DWord("EstimatedSize", 2048));
        AddApp(RegHive.LocalMachine, RegView.Registry32, Uninstall, "App32",
            RegValue.String("DisplayName", "App 32"), RegValue.String("UninstallString", "u.exe"));
        AddApp(RegHive.Users, RegView.Default, $@"{Bob}\{Uninstall}", "BobApp",
            RegValue.String("DisplayName", "Bob App"), RegValue.String("UninstallString", "u.exe"));
        AddApp(RegHive.Users, RegView.Default, $@"AppMover_{Alice}\{Uninstall}", "AliceApp",
            RegValue.String("DisplayName", "Alice App"), RegValue.String("UninstallString", "u.exe"));

        var apps = CreateService().GetInstalledApps(new InventoryOptions());

        Assert.Equal(["App 32", "App 64", "Bob App"], apps.Select(a => a.DisplayName));
        var app64 = apps.Single(a => a.KeyName == "App64");
        Assert.Equal(@"HKLM64\App64", app64.Id);
        Assert.Equal(AppScope.Machine, app64.Scope);
        Assert.Equal(2048, app64.EstimatedSizeKb);
        Assert.Equal(RegView.Registry32, apps.Single(a => a.KeyName == "App32").View);
        var bobApp = apps.Single(a => a.KeyName == "BobApp");
        Assert.Equal(AppScope.User, bobApp.Scope);
        Assert.Equal(Bob, bobApp.UserSid);
        Assert.Empty(_mounter.Mounted);
    }

    [Fact]
    public void AllUsersMountsOfflineHives()
    {
        AddApp(RegHive.Users, RegView.Default, $@"AppMover_{Alice}\{Uninstall}", "AliceApp",
            RegValue.String("DisplayName", "Alice App"), RegValue.String("UninstallString", "u.exe"));

        var apps = CreateService().GetInstalledApps(new InventoryOptions { AllUsers = true });

        var alice = Assert.Single(apps);
        Assert.Equal($@"HKU\{Alice}\AliceApp", alice.Id);
        Assert.Equal(Alice, alice.UserSid);
        Assert.Equal([Alice], _mounter.Mounted);
        Assert.Equal(1, _mounter.Disposed);
    }

    [Fact]
    public void HidesEntriesLikeAppsAndFeatures()
    {
        AddApp(RegHive.LocalMachine, RegView.Registry64, Uninstall, "Visible",
            RegValue.String("DisplayName", "Visible"), RegValue.String("UninstallString", "u.exe"));
        AddApp(RegHive.LocalMachine, RegView.Registry64, Uninstall, "MsiNoUninstallString",
            RegValue.String("DisplayName", "Msi"), RegValue.DWord("WindowsInstaller", 1));
        AddApp(RegHive.LocalMachine, RegView.Registry64, Uninstall, "NoName",
            RegValue.String("UninstallString", "u.exe"));
        AddApp(RegHive.LocalMachine, RegView.Registry64, Uninstall, "System",
            RegValue.String("DisplayName", "System"), RegValue.String("UninstallString", "u.exe"), RegValue.DWord("SystemComponent", 1));
        AddApp(RegHive.LocalMachine, RegView.Registry64, Uninstall, "KB123",
            RegValue.String("DisplayName", "Update"), RegValue.String("UninstallString", "u.exe"), RegValue.String("ParentKeyName", "Office"));
        AddApp(RegHive.LocalMachine, RegView.Registry64, Uninstall, "Hotfix",
            RegValue.String("DisplayName", "Hotfix"), RegValue.String("UninstallString", "u.exe"), RegValue.String("ReleaseType", "Security Update"));
        AddApp(RegHive.LocalMachine, RegView.Registry64, Uninstall, "NoUninstall",
            RegValue.String("DisplayName", "No uninstall"));

        var service = CreateService();
        var visible = service.GetInstalledApps(new InventoryOptions());
        var all = service.GetInstalledApps(new InventoryOptions { IncludeHidden = true });

        Assert.Equal(["Msi", "Visible"], visible.Select(a => a.DisplayName));
        Assert.Equal(7, all.Count);
        Assert.Equal("SystemComponent=1", all.Single(a => a.KeyName == "System").HiddenReason);
        Assert.Equal("No DisplayName", all.Single(a => a.KeyName == "NoName").HiddenReason);
    }

    [Fact]
    public void KeepsRawValues()
    {
        AddApp(RegHive.LocalMachine, RegView.Registry64, Uninstall, "App",
            RegValue.String("DisplayName", "  App  "), RegValue.String("UninstallString", "u.exe"),
            RegValue.String("EstimatedSize", "10"), RegValue.Bytes("Blob", [1, 2]));

        var app = Assert.Single(CreateService().GetInstalledApps(new InventoryOptions()));

        Assert.Equal("App", app.DisplayName);
        Assert.Equal(10, app.EstimatedSizeKb);
        Assert.Equal([1, 2], app.RawValues["blob"].Binary);
    }
}
