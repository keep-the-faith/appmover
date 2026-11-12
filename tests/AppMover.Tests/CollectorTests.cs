using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Registry;
using AppMover.Tests.Fakes;
using static AppMover.Tests.Fakes.TestMachine;

namespace AppMover.Tests;

public class CollectorTests
{
    private readonly TestMachine _machine = new TestMachine().WithAcmeEditor();
    private readonly CollectionResult _result;

    public CollectorTests()
    {
        using var session = _machine.OpenSession();
        _result = _machine.CollectionService().Collect(_machine.App, session);
    }

    private CollectedItem Find(string location) =>
        Assert.Single(_result.Items, i => i.Location.Equals(location, StringComparison.OrdinalIgnoreCase));

    private void Absent(string fragment) =>
        Assert.DoesNotContain(_result.Items, i => i.Location.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void InstallRoot()
    {
        var root = Assert.Single(_result.InstallRoots);
        Assert.Equal(InstallDir, root.Path);
        Assert.True(root.IsPrimary);

        var item = Find(InstallDir + @"\");
        Assert.Equal(ItemRole.Application, item.Role);
        Assert.Equal(Confidence.Certain, item.Confidence);
        Assert.True(item.Selected);
    }

    [Fact]
    public void UninstallEntry() =>
        Assert.Equal(ItemRole.UninstallEntry, Find($@"HKLM64\{Uninstall}\AcmeEditor").Role);

    [Fact]
    public void DataFolders()
    {
        Assert.Equal(Confidence.Probable, Find(@"C:\ProgramData\Acme Editor\").Confidence);
        Assert.Equal(Confidence.Probable, Find(@"C:\Users\bob\AppData\Roaming\Acme\Editor\").Confidence);
        Absent(@"Acme\Other");
        // The publisher folder itself is not proposed: a product sub-folder was found.
        Assert.DoesNotContain(_result.Items, i => i.Location == @"C:\Users\bob\AppData\Roaming\Acme\");
        Absent("Edge");

        var cache = Find(@"C:\Users\bob\AppData\Local\EditorCache\");
        Assert.Equal(Confidence.Possible, cache.Confidence);
        Assert.False(cache.Selected);

        var documents = Find(@"C:\Users\bob\Documents\Acme Editor\");
        Assert.Equal(Confidence.Possible, documents.Confidence);
        Assert.Equal(Bob, documents.UserSid);

        Assert.Equal(Confidence.Certain, Find(@"C:\Users\bob\AppData\Local\VirtualStore\Program Files\Acme\Editor\").Confidence);
    }

    [Fact]
    public void Shortcuts()
    {
        // The Start menu folder is taken as a whole: the shortcut inside is merged into it.
        Assert.Equal(ItemRole.Shortcut, Find(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Editor\").Role);
        Absent(@"Acme Editor\Acme Editor.lnk");
        Absent("Notepad.lnk");

        var desktop = Find(@"C:\Users\bob\Desktop\Editor.lnk");
        Assert.Equal(Confidence.Certain, desktop.Confidence);
        Assert.Equal($@"{InstallDir}\editor.exe", desktop.Details!["Target"]);
        Assert.Equal("--new", desktop.Details["Arguments"]);
    }

    [Fact]
    public void AdvertisedMsiShortcut()
    {
        const string productCode = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";
        var machine = new TestMachine();
        machine.Files
            .Add(@"C:\Program Files\Acme\Editor\editor.exe")
            .Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Editor.lnk",
                ShellLinkBuilder.Advertised("darwin", $@"C:\Windows\Installer\{productCode}\editor.ico"))
            .Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Editor Help.lnk",
                ShellLinkBuilder.Advertised("darwin", @"C:\Windows\Installer\{OTHER}\help.ico"));
        var app = machine.App;
        app.Installer = new InstallerInfo
        {
            Type = InstallerType.Msi, Confidence = 100, Evidence = [],
            Properties = new Dictionary<string, string> { [InstallerProperties.ProductCode] = productCode },
        };

        using var session = machine.OpenSession();
        var items = machine.CollectionService().Collect(app, session).Items;

        Assert.Contains(items, i => i.Location.EndsWith(@"Programs\Acme Editor.lnk") && i.Confidence == Confidence.Certain);
        Assert.DoesNotContain(items, i => i.Location.EndsWith("Help.lnk"));
    }

    [Fact]
    public void ScheduledTask()
    {
        var task = Find(@"C:\Windows\System32\Tasks\Acme\Update");
        Assert.Equal(ItemRole.ScheduledTask, task.Role);
        Assert.Equal(@"\Acme\Update", task.Details!["TaskName"]);
        Assert.Equal(Confidence.Certain, task.Confidence);
        Absent(@"Tasks\Other");
    }

    [Fact]
    public void ScheduledTaskWithComHandlerOfTheApplication()
    {
        var task = Find(@"C:\Windows\System32\Tasks\Acme\Sync");
        Assert.Equal(Confidence.Certain, task.Confidence);
        Assert.Contains(ClsId, task.Reason);
        Absent("OtherCom");
    }

    [Fact]
    public void ScheduledTaskInDataFolderKeepsTheFolderConfidence()
    {
        var task = Find(@"C:\Windows\System32\Tasks\Acme\Cleanup");
        Assert.Equal(Confidence.Possible, task.Confidence);
        Assert.False(task.Selected);
    }

    [Fact]
    public void ServiceInDataFolderIsProbable()
    {
        var updater = Find(@"HKLM64\SYSTEM\CurrentControlSet\Services\AcmeUpdater");
        Assert.Equal(ItemRole.Service, updater.Role);
        Assert.Equal(Confidence.Probable, updater.Confidence);
        Assert.True(updater.Selected);
        Assert.Contains(@"C:\ProgramData\Acme Editor", updater.Reason);
    }

    [Fact]
    public void ApplicationRegistryKeys()
    {
        Assert.Equal(Confidence.Probable, Find(@"HKLM64\SOFTWARE\Acme\Editor").Confidence);
        Absent(@"Acme\Editor\Recent"); // included in its parent
        Absent(@"Acme\Shared");
        var user = Find($@"HKU\{Bob}\Software\Acme\Editor");
        Assert.Equal(Bob, user.UserSid);
    }

    [Fact]
    public void SystemRegistryEntries()
    {
        var service = Find(@"HKLM64\SYSTEM\CurrentControlSet\Services\AcmeSvc");
        Assert.Equal(ItemRole.Service, service.Role);
        Absent(@"Services\Other");

        Assert.Equal(ItemRole.Startup, Find(@"HKLM32\SOFTWARE\Microsoft\Windows\CurrentVersion\Run → AcmeTray").Role);
        Absent("→ Other");
        Assert.Equal(ItemRole.AppPath, Find(@"HKLM64\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\editor.exe").Role);

        var path = Assert.Single(_result.Items, i => i.Kind == ItemKind.PathEntry);
        Assert.Equal("Path", path.ValueName);
        Assert.Equal($@"{InstallDir}\bin", path.Entry);
        Assert.Equal(ItemRole.Environment, Find(@"HKLM64\SYSTEM\CurrentControlSet\Control\Session Manager\Environment → ACME_HOME").Role);

        Assert.Equal(ItemRole.FirewallRule, Find(@"HKLM64\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules → {rule-acme}").Role);
        Absent("rule-other");
    }

    [Fact]
    public void Classes()
    {
        Assert.Equal(ItemRole.Com, Find($@"HKLM64\SOFTWARE\Classes\CLSID\{ClsId}").Role);
        Assert.Equal(ItemRole.Com, Find(@"HKLM64\SOFTWARE\Classes\Acme.Shell.1").Role);
        Assert.Equal(Confidence.Certain, Find(@"HKLM64\SOFTWARE\Classes\AcmeEditor.Document").Confidence);
        Absent("txtfile");

        var defaultProgram = Find(@"HKLM64\SOFTWARE\Classes\.acme → (Default)");
        Assert.Equal(Confidence.Probable, defaultProgram.Confidence);
        Assert.NotNull(defaultProgram.Warning);
        Assert.Equal(Confidence.Certain, Find(@"HKLM64\SOFTWARE\Classes\.txt\OpenWithProgids → AcmeEditor.Document").Confidence);
        Absent(@".txt → (Default)");

        Assert.Contains("URL protocol", Find($@"HKU\{Bob}\Software\Classes\acme").Reason);
    }

    [Fact]
    public void Keywords()
    {
        Assert.Contains("acmeeditor", _result.Keywords.Products);
        Assert.Contains("editor", _result.Keywords.Products);
        Assert.Contains("acme", _result.Keywords.Publishers);
    }
}

public class InstallRootTests
{
    private readonly TestMachine _machine = new();

    private IReadOnlyList<InstallRoot> Resolve(InstalledApp app)
    {
        using var session = _machine.OpenSession();
        return _machine.CollectionService().Collect(app, session).InstallRoots;
    }

    [Fact]
    public void RejectsTooBroadFolders()
    {
        _machine.Files.Add(@"C:\Program Files\x.txt").Add(@"C:\Users\bob\AppData\Local\y.txt").Add(@"C:\Windows\System32\z.dll");
        var app = _machine.App;

        Assert.Empty(Resolve(new InstalledApp
        {
            Id = app.Id, KeyName = app.KeyName, RegistryPath = app.RegistryPath, Scope = app.Scope, View = app.View, RawValues = app.RawValues,
            InstallLocation = @"C:\Program Files", DisplayIcon = @"C:\Windows\System32\z.dll,0",
            Installer = new InstallerInfo { Type = InstallerType.Unknown, Confidence = 0, Evidence = [], UninstallerPath = @"C:\Users\bob\AppData\Local\uninst.exe" },
        }));
    }

    [Fact]
    public void MergesNestedFoldersAndKeepsBestConfidence()
    {
        _machine.Files.Add(@"C:\Apps\Tool\bin\tool.exe").Add(@"C:\Apps\Tool\uninstall.exe");
        var app = _machine.App;

        var roots = Resolve(new InstalledApp
        {
            Id = app.Id, KeyName = app.KeyName, RegistryPath = app.RegistryPath, Scope = app.Scope, View = app.View, RawValues = app.RawValues,
            InstallLocation = @"C:\Apps\Tool\bin\",
            DisplayIcon = "\"C:\\Apps\\Tool\\bin\\tool.exe\",0",
            Installer = new InstallerInfo { Type = InstallerType.Unknown, Confidence = 0, Evidence = [], UninstallerPath = @"C:\Apps\Tool\uninstall.exe" },
        });

        var root = Assert.Single(roots);
        Assert.Equal(@"C:\Apps\Tool", root.Path);
        Assert.Equal(Confidence.Certain, root.Confidence);
        Assert.True(root.IsPrimary);
    }

    [Theory]
    [InlineData(@"C:\App\app.exe,0", @"C:\App\app.exe")]
    [InlineData("\"C:\\App\\app.exe\",-101", @"C:\App\app.exe")]
    [InlineData(@"C:\My App\icon.ico", @"C:\My App\icon.ico")]
    public void StripIconIndex(string icon, string expected) => Assert.Equal(expected, InstallRootResolver.StripIconIndex(icon));
}

public class MergeTests
{
    private static CollectedItem Dir(string path, Confidence confidence) => new()
    {
        Kind = ItemKind.Directory, Role = ItemRole.Data, Confidence = confidence, Collector = "t", Reason = "r", Path = path,
    };

    private static CollectedItem Key(string path, Confidence confidence, string? value = null) => new()
    {
        Kind = value is null ? ItemKind.RegistryKey : ItemKind.RegistryValue, Role = ItemRole.Settings, Confidence = confidence,
        Collector = "t", Reason = "r", Registry = RegistryLocation.Machine(RegView.Registry64, path), ValueName = value,
    };

    [Fact]
    public void KeepsBestDuplicate()
    {
        var merged = CollectionService.Merge([Dir(@"C:\A", Confidence.Possible), Dir(@"c:\a", Confidence.Certain)]);

        Assert.Equal(Confidence.Certain, Assert.Single(merged).Confidence);
    }

    [Fact]
    public void RemovesContainedItemsUnlessMoreConfident()
    {
        var merged = CollectionService.Merge(
        [
            Dir(@"C:\A", Confidence.Probable),
            Dir(@"C:\A\B", Confidence.Possible),
            Dir(@"C:\A\C", Confidence.Certain),
            Key(@"SOFTWARE\X", Confidence.Certain),
            Key(@"SOFTWARE\X\Y", Confidence.Probable),
            Key(@"SOFTWARE\X", Confidence.Probable, "v"),
            Key(@"SOFTWARE\XY", Confidence.Possible),
        ]);

        Assert.Equal([@"C:\A\", @"C:\A\C\", @"HKLM64\SOFTWARE\X", @"HKLM64\SOFTWARE\XY"], merged.Select(i => i.Location).Order(StringComparer.Ordinal));
        Assert.False(merged.Single(i => i.Location == @"HKLM64\SOFTWARE\XY").Selected);
        Assert.True(merged.Single(i => i.Location == @"C:\A\").Selected);
    }
}
