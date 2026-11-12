using AppMover.Core.Registry;
using AppMover.Deployment;
using AppMover.Packaging;
using AppMover.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static AppMover.Tests.Fakes.TestMachine;

namespace AppMover.Tests;

/// <summary>Package Acme Editor on bob's machine, deploy it on alice's machine (Program Files on D:), uninstall it.</summary>
public sealed class DeploymentTests : IDisposable
{
    private const string Alice = "S-1-5-21-9-9-9-1001";
    private const string TargetDir = @"D:\Programs\Acme\Editor";
    private const string StoreRoot = @"C:\ProgramData\AppMover\Deployments";
    /// <summary>AppMover's own folder: not part of the comparisons.</summary>
    private const string AppMoverData = @"C:\ProgramData\AppMover";
    private const string UninstallCommand = "\"C:\\Program Files\\AppMover\\appmover.exe\" uninstall";

    private readonly string _packagePath = Path.Combine(AppContext.BaseDirectory, "test-output", $"{Guid.NewGuid():N}{PackageManifest.Extension}");
    private readonly TestMachine _target = new(Alice, "alice", @"D:\Programs");
    private readonly FakeSystem _system;
    private readonly DeploymentStore _store;
    private readonly PackageReader _package;

    public DeploymentTests()
    {
        var source = new TestMachine().WithAcmeEditor();
        Directory.CreateDirectory(Path.GetDirectoryName(_packagePath)!);
        using (var session = source.OpenSession())
        {
            var collection = source.CollectionService().Collect(source.App, session);
            new PackageWriter(source.Registry, source.Files, NullLogger<PackageWriter>.Instance)
                .Write(_packagePath, source.App, collection, collection.Items, session);
        }

        _system = new FakeSystem(_target);
        _store = new DeploymentStore(_target.Files, StoreRoot);
        _package = PackageReader.Open(_packagePath);
    }

    public void Dispose()
    {
        _package.Dispose();
        File.Delete(_packagePath);
    }

    private DeploymentPlan Plan(DeployOptions? options = null)
    {
        using var session = _target.OpenSession();
        return new DeploymentPlanner(_target.Registry, _target.Files, _system.Value).Plan(_package, session,
            options ?? new DeployOptions { UserMap = new Dictionary<int, string> { [0] = Alice }, UninstallCommand = UninstallCommand });
    }

    private DeploymentExecutor Executor() =>
        new(_target.Registry, _target.Registry, _target.Files, _system.Value, _store, NullLogger<DeploymentExecutor>.Instance);

    private DeploymentRecord Deploy(DeploymentPlan? plan = null)
    {
        plan ??= Plan();
        using var session = _target.OpenSession();
        return Executor().Deploy(plan, _package, session);
    }

    private string? Value(RegHive hive, RegView view, string path, string name)
    {
        using var key = _target.Registry.OpenKey(hive, view, path);
        return key?.GetValue(name)?.AsString();
    }

    [Fact]
    public void PackageIsValid() => Assert.Empty(_package.Validate(verifyHashes: true));

    [Theory]
    [InlineData(@"{InstallDir}\a.exe", true)]
    [InlineData(@"C:\App\a.exe", true)]
    [InlineData(@"\\server\share\a", true)]
    [InlineData(@"{InstallDir}\..\..\Windows\evil.dll", false)]
    [InlineData(@"C:\App\..\Windows", false)]
    [InlineData(@"relative\path", false)]
    [InlineData("{}", false)]
    public void SafePaths(string path, bool expected) => Assert.Equal(expected, PackageReader.IsSafePath(path));

    [Fact]
    public void PlanRelocatesTheInstallDirectory()
    {
        var plan = Plan();

        Assert.True(plan.CanProceed);
        Assert.Equal(TargetDir, plan.InstallDir);
        Assert.Equal(TargetDir, plan.Target.Folders["InstallDir"]);
        Assert.Equal(@"C:\Users\alice\AppData\Roaming", plan.Target.Folders["U0.AppData"]);
        Assert.Contains(plan.Operations, o => o is ExtractFileOperation { Target: $@"{TargetDir}\editor.exe" });
        Assert.Contains(plan.Operations, o => o is CreateDirectoryOperation { Path: $@"{TargetDir}\empty" });
        // Task XML files are registered, never copied into System32\Tasks.
        Assert.DoesNotContain(plan.Operations, o => o is ExtractFileOperation e && e.Target.Contains(@"System32\Tasks"));
        Assert.Contains(plan.Issues, i => i.Message.Contains("Firewall"));
        // Detected at export (native.exe imports vcruntime140.dll), missing on the target.
        Assert.Contains(plan.Issues, i => i.Message.Contains("Missing prerequisite: Microsoft Visual C++ 2015-2022 Redistributable (x64)"));
    }

    [Fact]
    public void DeploysFilesAndRegistryOnTheTarget()
    {
        var record = Deploy();

        Assert.Equal(DeploymentState.Deployed, record.State);
        var files = _target.Files;
        Assert.Equal("EDITOR", files.ReadText($@"{TargetDir}\editor.exe"));
        Assert.Equal(FakeFileSystem.LastWrite, files.GetFileMeta($@"{TargetDir}\editor.exe")!.LastWriteUtc);
        Assert.Equal("PLUGIN", files.ReadText($@"{TargetDir}\plugins\a.dll"));
        Assert.True(files.DirectoryExists($@"{TargetDir}\empty"));
        Assert.Equal("theme=dark", files.ReadText(@"C:\Users\alice\AppData\Roaming\Acme\Editor\settings.ini"));
        Assert.Equal("LICENSE", files.ReadText(@"C:\ProgramData\Acme Editor\license.dat"));
        Assert.False(files.FileExists(@"C:\Windows\System32\Tasks\Acme\Update"));

        var lm = RegHive.LocalMachine;
        Assert.Equal(TargetDir, Value(lm, RegView.Registry64, @"SOFTWARE\Acme\Editor", "Path"));
        using (var recent = _target.Registry.OpenKey(lm, RegView.Registry64, @"SOFTWARE\Acme\Editor\Recent"))
            Assert.Equal([$@"{TargetDir}\samples\a.acme", @"D:\b.acme"], recent!.GetValue("Files")!.Multi!);
        Assert.Equal("fr", Value(RegHive.Users, RegView.Default, $@"{Alice}\Software\Acme\Editor", "Language"));
        Assert.Equal($"\"{TargetDir}\\tray.exe\" /min", Value(lm, RegView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "AcmeTray"));
        Assert.Equal($"\"{TargetDir}\\editor.exe\" \"%1\"", Value(lm, RegView.Registry64, @"SOFTWARE\Classes\AcmeEditor.Document\shell\open\command", ""));
        Assert.Equal($"%SystemRoot%;{TargetDir}\\bin", Value(lm, RegView.Registry64, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path"));
        Assert.Equal(TargetDir, Value(lm, RegView.Registry64, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "ACME_HOME"));
        Assert.Equal(1, _system.Notifier.Environment);
        Assert.Equal(1, _system.Notifier.Associations);
    }

    [Fact]
    public void RegistersTheUninstallEntryOfAppMover()
    {
        var record = Deploy();

        var key = $@"{Uninstall}\AcmeEditor";
        Assert.Equal(TargetDir, Value(RegHive.LocalMachine, RegView.Registry64, key, "InstallLocation"));
        Assert.Equal($"{UninstallCommand} {record.Id}", Value(RegHive.LocalMachine, RegView.Registry64, key, "UninstallString"));
        Assert.Equal("0", Value(RegHive.LocalMachine, RegView.Registry64, key, "WindowsInstaller"));
        Assert.Equal(record.Id, Value(RegHive.LocalMachine, RegView.Registry64, key, "AppMoverDeploymentId"));
    }

    [Fact]
    public void CreatesServicesWithRelocatedPaths()
    {
        Deploy();

        Assert.Equal($"\"{TargetDir}\\svc.exe\"", _system.Services.Created["AcmeSvc"].ImagePath);
        Assert.Equal(16u, _system.Services.Created["AcmeSvc"].Type);
        Assert.Equal("\"C:\\ProgramData\\Acme Editor\\updater.exe\" /svc", _system.Services.Created["AcmeUpdater"].ImagePath);
        Assert.False(_system.Services.Created.ContainsKey("Existing"));
    }

    [Fact]
    public void StartsTheServicesThatStartWithWindows()
    {
        var record = Deploy();

        Assert.Equal(["AcmeSvc"], _system.Services.Started); // AcmeUpdater is started on demand
        Assert.Empty(record.Warnings);
    }

    [Fact]
    public void ServiceThatFailsToStartIsReportedNotRolledBack()
    {
        _system.Services.FailToStart = "AcmeSvc";

        var record = Deploy();

        Assert.Equal(DeploymentState.Deployed, record.State);
        Assert.True(_system.Services.Created.ContainsKey("AcmeSvc"));
        Assert.Contains("AcmeSvc", Assert.Single(record.Warnings));
    }

    [Fact]
    public void RegistersTasksForTheTargetUser()
    {
        Deploy();

        var update = _system.Tasks.Tasks[@"\Acme\Update"];
        Assert.Contains($@"<Command>{TargetDir}\editor.exe</Command>", update);
        Assert.Contains($"<UserId>{Alice}</UserId>", update);
        Assert.DoesNotContain(Bob, update);
        Assert.Contains(ClsId, _system.Tasks.Tasks[@"\Acme\Sync"]);
        Assert.False(_system.Tasks.Tasks.ContainsKey(@"\Acme\Cleanup")); // "Possible": not packaged
    }

    [Fact]
    public void RecreatesShortcutsWhoseTargetMoved()
    {
        Deploy();

        var desktop = _system.Shortcuts.Created[@"C:\Users\alice\Desktop\Editor.lnk"];
        Assert.Equal($@"{TargetDir}\editor.exe", desktop.Target);
        Assert.Equal("--new", desktop.Arguments);
        Assert.Contains(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Editor\Acme Editor.lnk", _system.Shortcuts.Created.Keys);
    }

    [Fact]
    public void UninstallRestoresTheInitialState()
    {
        _target.Files.Add($@"{TargetDir}\editor.exe", "OLD"); // an older copy is replaced, then restored
        var registryBefore = _target.Registry.Dump();
        var filesBefore = _target.Files.Snapshot(excluded: AppMoverData);

        var record = Deploy();
        Assert.Equal("EDITOR", _target.Files.ReadText($@"{TargetDir}\editor.exe"));

        using (var session = _target.OpenSession())
            Assert.Empty(Executor().Uninstall(_store.Load(record.Id)!, session));

        Assert.Equal(registryBefore, _target.Registry.Dump());
        Assert.Equal(filesBefore, _target.Files.Snapshot(excluded: AppMoverData));
        Assert.Empty(_system.Services.Created);
        Assert.Empty(_system.Tasks.Tasks);
        Assert.Null(_store.Load(record.Id));
    }

    [Fact]
    public void NewerVersionedFilesAreNotReplaced()
    {
        var newer = PeBuilder.Build(Core.Pe.PeInfo.MachineX64, ["kernel32.dll"], version: new Version(2, 0, 0, 0));
        _target.Files.Add($@"{TargetDir}\plugins\engine.dll", newer);

        var plan = Plan();
        Deploy(plan);

        Assert.Equal(newer, _target.Files.OpenRead($@"{TargetDir}\plugins\engine.dll").ReadAllBytesForTest());
        var kept = Assert.Single(plan.Issues, i => i.Message.StartsWith("Kept the newer file"));
        Assert.Contains("2.0.0.0", kept.Message);
        Assert.DoesNotContain(plan.Operations, o => o is ExtractFileOperation e && e.Target.EndsWith("engine.dll"));
    }

    [Fact]
    public void OlderVersionedFilesAreReplaced()
    {
        _target.Files.Add($@"{TargetDir}\plugins\engine.dll", PeBuilder.Build(Core.Pe.PeInfo.MachineX64, ["kernel32.dll"], version: new Version(1, 0, 0, 0)));

        Deploy();

        using var stream = _target.Files.OpenRead($@"{TargetDir}\plugins\engine.dll");
        Assert.Equal(new Version(1, 5, 0, 0), Core.Pe.PeReader.Read(stream)!.FileVersion);
    }

    [Fact]
    public void FailureRollsEverythingBack()
    {
        var registryBefore = _target.Registry.Dump();
        var filesBefore = _target.Files.Snapshot(excluded: AppMoverData);
        _system.Services.FailOn = "AcmeUpdater";
        var plan = Plan();

        var error = Assert.Throws<DeploymentException>(() => Deploy(plan));

        Assert.Contains("Access denied", error.Message);
        Assert.Empty(error.RollbackFailures);
        Assert.Equal(registryBefore, _target.Registry.Dump());
        Assert.Equal(filesBefore, _target.Files.Snapshot(excluded: AppMoverData));
        Assert.Empty(_system.Services.Created);
        Assert.Equal(DeploymentState.RolledBack, _store.Load(plan.Id)!.State);
    }

    [Fact]
    public void AlreadyInstalledNeedsForce()
    {
        _target.Registry.Set(RegHive.LocalMachine, $@"{Uninstall}\AcmeEditor", RegValue.String("DisplayName", "Acme Editor 1.0"));

        Assert.False(Plan().CanProceed);
        var forced = Plan(options: new DeployOptions
        {
            UserMap = new Dictionary<int, string> { [0] = Alice }, UninstallCommand = UninstallCommand, Force = true,
        });
        Assert.True(forced.CanProceed);
        Assert.Contains(forced.Issues, i => i.Message.Contains("already installed"));
    }

    [Fact]
    public void UnmappedUserIsSkipped()
    {
        var plan = Plan(options: new DeployOptions { UserMap = new Dictionary<int, string>(), UninstallCommand = UninstallCommand });

        Assert.True(plan.CanProceed);
        Assert.Contains(plan.Issues, i => i.Message.Contains("not mapped"));
        Assert.DoesNotContain(plan.Operations, o => o is ExtractFileOperation e && e.Target.Contains(@"\Users\"));
        Assert.DoesNotContain(plan.Operations, o => o is WriteRegistryOperation { Location.Hive: RegHive.Users });
    }

    [Fact]
    public void ProtectedExistingKeyIsLeftAsIs()
    {
        _target.Registry.Set(RegHive.LocalMachine, @"SOFTWARE\Acme\Editor", RegValue.String("Path", @"C:\Windows owned"));
        _target.Registry.Protected.Add(@"SOFTWARE\Acme\Editor");

        var record = Deploy();

        Assert.Equal(DeploymentState.Deployed, record.State);
        Assert.Equal(@"C:\Windows owned", Value(RegHive.LocalMachine, RegView.Registry64, @"SOFTWARE\Acme\Editor", "Path"));
        Assert.Contains(@"SOFTWARE\Acme\Editor", Assert.Single(record.Warnings));
        Assert.DoesNotContain(record.Journal, u => u.Registry?.Path == @"SOFTWARE\Acme\Editor" && u.ValueName == "Path");
    }

    [Fact]
    public void IdenticalValueIsNotRewritten()
    {
        _target.Registry.Set(RegHive.LocalMachine, @"SOFTWARE\Acme\Editor", RegValue.String("Path", TargetDir));
        _target.Registry.Protected.Add(@"SOFTWARE\Acme\Editor");

        var record = Deploy();

        Assert.Empty(record.Warnings);
        Assert.DoesNotContain(record.Journal, u => u.Registry?.Path == @"SOFTWARE\Acme\Editor" && u.ValueName == "Path");
    }

    [Fact]
    public void CustomInstallDirectory()
    {
        var plan = Plan(options: new DeployOptions
        {
            InstallDir = @"E:\Apps\Acme\", UserMap = new Dictionary<int, string> { [0] = Alice }, UninstallCommand = UninstallCommand,
        });
        Deploy(plan);

        Assert.Equal("EDITOR", _target.Files.ReadText(@"E:\Apps\Acme\editor.exe"));
        Assert.Equal(@"E:\Apps\Acme", Value(RegHive.LocalMachine, RegView.Registry64, @"SOFTWARE\Acme\Editor", "Path"));
    }

    [Fact]
    public void NotEnoughDiskSpaceIsBlocking()
    {
        _target.Files.AvailableSpace = 3;

        Assert.False(Plan().CanProceed);
    }
}
