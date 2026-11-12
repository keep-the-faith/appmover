using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Inventory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppMover.Tests.Fakes;

/// <summary>
/// A fake Windows machine with one user (bob) and "Acme Editor" installed in C:\Program Files\Acme\Editor,
/// integrated everywhere an application can be (services, shortcuts, COM, associations, data folders…).
/// </summary>
public sealed class TestMachine
{
    public const string Bob = "S-1-5-21-1-2-3-1001";
    public const string InstallDir = @"C:\Program Files\Acme\Editor";
    public const string ClsId = "{11111111-2222-3333-4444-555555555555}";
    public const string Uninstall = InventoryService.UninstallPath;

    private const string StartMenu = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";
    private const string Classes = @"SOFTWARE\Classes";

    public FakeRegistry Registry { get; } = new();
    public FakeFileSystem Files { get; } = new();
    public FakePaths Paths { get; } = new();

    /// <param name="userSid">The only user of the machine.</param>
    /// <param name="userName">Its profile folder name.</param>
    /// <param name="programFiles">Program Files location, when not the default one.</param>
    /// <param name="loggedOn">False: the user's hive is not loaded (mounted as AppMover_&lt;SID&gt; by the fake mounter).</param>
    public TestMachine(string userSid = Bob, string userName = "bob", string? programFiles = null, bool loggedOn = true)
    {
        UserSid = userSid;
        Registry
            .Set(RegHive.LocalMachine, $@"{UserProfileProvider.ProfileListPath}\{userSid}", RegValue.ExpandString("ProfileImagePath", $@"%SystemDrive%\Users\{userName}"))
            .Set(RegHive.Users, loggedOn ? userSid : $"AppMover_{userSid}")
            .Set(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Existing", RegValue.ExpandString("ImagePath", @"%SystemRoot%\existing.exe"))
            .Set(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", RegValue.ExpandString("Path", "%SystemRoot%"));
        Files.AddDirectory(@"C:\ProgramData");
        if (programFiles is not null)
            Registry.Set(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion", RegValue.String("ProgramFilesDir", programFiles));
    }

    public string UserSid { get; }

    public InstalledApp App { get; } = new()
    {
        Id = @"HKLM64\AcmeEditor",
        KeyName = "AcmeEditor",
        RegistryPath = $@"HKEY_LOCAL_MACHINE\{Uninstall}\AcmeEditor",
        Scope = AppScope.Machine,
        View = RegView.Registry64,
        DisplayName = "Acme Editor 2.1 (x64)",
        DisplayVersion = "2.1",
        Publisher = "Acme Software Inc.",
        InstallLocation = InstallDir,
        UninstallString = $"\"{InstallDir}\\uninst.exe\"",
        RawValues = new Dictionary<string, RegValue>(),
        Installer = new InstallerInfo
        {
            Type = InstallerType.Nsis,
            Confidence = 80,
            Evidence = [],
            UninstallerPath = $@"{InstallDir}\uninst.exe",
        },
    };

    /// <summary>Installs Acme Editor and some unrelated things.</summary>
    public TestMachine WithAcmeEditor()
    {
        Files
            .Add($@"{InstallDir}\editor.exe", "EDITOR")
            .Add($@"{InstallDir}\uninst.exe", "NullsoftInst")
            .Add($@"{InstallDir}\native.exe", PeBuilder.Build(Core.Pe.PeInfo.MachineX64, ["kernel32.dll", "vcruntime140.dll"]))
            .Add($@"{InstallDir}\plugins\a.dll", "PLUGIN")
            .Add($@"{InstallDir}\plugins\engine.dll", PeBuilder.Build(Core.Pe.PeInfo.MachineX64, ["kernel32.dll"], version: new Version(1, 5, 0, 0)))
            .AddDirectory($@"{InstallDir}\empty")
            .Add(@"C:\Users\bob\AppData\Roaming\Acme\Editor\settings.ini", "theme=dark")
            .Add(@"C:\Users\bob\AppData\Roaming\Acme\Editor\paths.ini", "[Paths]\r\nHome=C:\\Program Files\\Acme\\Editor\r\nData=C:\\Users\\bob\\AppData\\Roaming\\Acme\\Editor\r\n")
            .Add($@"{InstallDir}\config.json",
                """{ "plugins": "C:\\Program Files\\Acme\\Editor\\plugins", "cache": "C:/Users/bob/AppData/Local/EditorCache" }""")
            .Add(@"C:\Users\bob\AppData\Roaming\Acme\Other\other.ini")
            .Add(@"C:\Users\bob\AppData\Local\EditorCache\cache.bin")
            .Add(@"C:\Users\bob\AppData\Local\Microsoft\Edge\x")
            .Add(@"C:\ProgramData\Acme Editor\license.dat", "LICENSE")
            .Add(@"C:\Users\bob\Documents\Acme Editor\doc.acme")
            .Add(@"C:\Users\bob\AppData\Local\VirtualStore\Program Files\Acme\Editor\user.cfg")
            .Add($@"{StartMenu}\Acme Editor\Acme Editor.lnk", ShellLinkBuilder.Target($@"{InstallDir}\editor.exe"))
            .Add($@"{StartMenu}\Notepad.lnk", ShellLinkBuilder.Target(@"C:\Windows\notepad.exe"))
            .Add(@"C:\Users\bob\Desktop\Editor.lnk", ShellLinkBuilder.Target($@"{InstallDir}\editor.exe", "--new"))
            .Add(@"C:\Windows\System32\Tasks\Acme\Update", TaskXml($@"{InstallDir}\editor.exe", "--update"))
            .Add(@"C:\Windows\System32\Tasks\Other", TaskXml(@"C:\Windows\other.exe", ""))
            .Add(@"C:\Windows\System32\Tasks\Acme\Sync", ComTaskXml(ClsId))
            .Add(@"C:\Windows\System32\Tasks\Acme\Cleanup", TaskXml(@"C:\Users\bob\AppData\Local\EditorCache\clean.exe", ""))
            .Add(@"C:\Windows\System32\Tasks\OtherCom", ComTaskXml("{99999999-9999-9999-9999-999999999999}"));

        Registry
            .Set(RegHive.LocalMachine, $@"{Uninstall}\AcmeEditor",
                RegValue.String("DisplayName", "Acme Editor 2.1 (x64)"),
                RegValue.String("InstallLocation", InstallDir),
                RegValue.String("UninstallString", $"\"{InstallDir}\\uninst.exe\""),
                RegValue.DWord("EstimatedSize", 1234))
            .Set(RegHive.LocalMachine, @"SOFTWARE\Acme\Editor", RegValue.String("Path", InstallDir))
            .Set(RegHive.LocalMachine, @"SOFTWARE\Acme\Editor\Recent", RegValue.MultiString("Files", $@"{InstallDir}\samples\a.acme", @"D:\b.acme"))
            .Set(RegHive.LocalMachine, @"SOFTWARE\Acme\Shared")
            .Set(RegHive.Users, $@"{Bob}\Software\Acme\Editor", RegValue.String("Language", "fr"))
            .Set(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\AcmeSvc",
                RegValue.ExpandString("ImagePath", $"\"{InstallDir}\\svc.exe\""), RegValue.DWord("Type", 16), RegValue.DWord("Start", 2))
            .Set(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\AcmeUpdater",
                RegValue.ExpandString("ImagePath", "\"C:\\ProgramData\\Acme Editor\\updater.exe\" /svc"), RegValue.DWord("Type", 16))
            .Set(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Other", RegValue.ExpandString("ImagePath", @"%SystemRoot%\other.exe"))
            .Set(RegHive.LocalMachine, RegView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                RegValue.String("AcmeTray", $"\"{InstallDir}\\tray.exe\" /min"), RegValue.String("Other", @"C:\x.exe"))
            .Set(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\editor.exe", RegValue.String("", $@"{InstallDir}\editor.exe"))
            .Set(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
                RegValue.ExpandString("Path", $@"%SystemRoot%;{InstallDir}\bin;C:\Tools"), RegValue.String("ACME_HOME", InstallDir))
            .Set(RegHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules",
                RegValue.String("{rule-acme}", $@"v2.30|Action=Allow|Dir=In|App={InstallDir}\editor.exe|Name=Acme|"),
                RegValue.String("{rule-other}", @"v2.30|Action=Allow|App=C:\Windows\other.exe|"))
            .Set(RegHive.LocalMachine, $@"{Classes}\CLSID\{ClsId}\InprocServer32", RegValue.String("", $@"{InstallDir}\shell.dll"))
            .Set(RegHive.LocalMachine, $@"{Classes}\CLSID\{ClsId}\ProgID", RegValue.String("", "Acme.Shell.1"))
            .Set(RegHive.LocalMachine, $@"{Classes}\Acme.Shell.1", RegValue.String("", "Acme shell extension"))
            .Set(RegHive.LocalMachine, $@"{Classes}\AcmeEditor.Document\shell\open\command", RegValue.String("", $"\"{InstallDir}\\editor.exe\" \"%1\""))
            .Set(RegHive.LocalMachine, $@"{Classes}\txtfile\shell\open\command", RegValue.String("", @"C:\Windows\notepad.exe ""%1"""))
            .Set(RegHive.LocalMachine, $@"{Classes}\.acme", RegValue.String("", "AcmeEditor.Document"))
            .Set(RegHive.LocalMachine, $@"{Classes}\.txt", RegValue.String("", "txtfile"))
            .Set(RegHive.LocalMachine, $@"{Classes}\.txt\OpenWithProgids", RegValue.String("AcmeEditor.Document", ""), RegValue.String("txtfile", ""))
            .Set(RegHive.Users, $@"{Bob}\Software\Classes\acme", RegValue.String("URL Protocol", ""))
            .Set(RegHive.Users, $@"{Bob}\Software\Classes\acme\shell\open\command", RegValue.String("", $"\"{InstallDir}\\editor.exe\" --url \"%1\""));
        return this;
    }

    public CollectionSession OpenSession() => CollectionSession.Open(
        new UserProfileProvider(Registry, Paths), new FakeHiveMounter(), new KnownFoldersResolver(Registry, Paths), [UserSid], NullLogger.Instance);

    public CollectionService CollectionService() => new(Registry, Files, Paths, NullLogger<CollectionService>.Instance);

    private static string ComTaskXml(string clsid) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Actions Context="Author">
            <ComHandler>
              <ClassId>{clsid}</ClassId>
            </ComHandler>
          </Actions>
        </Task>
        """;

    private static string TaskXml(string command, string arguments) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals>
            <Principal id="Author">
              <UserId>{Bob}</UserId>
            </Principal>
          </Principals>
          <Actions Context="Author">
            <Exec>
              <Command>{command}</Command>
              <Arguments>{arguments}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
}
