using System.Text;
using AppMover.Core.Models;
using AppMover.Core.Nsis;
using AppMover.Core.Registry;
using AppMover.Core.Text;
using AppMover.Core.Tracing;
using AppMover.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static AppMover.Tests.Fakes.TestMachine;

namespace AppMover.Tests;

/// <summary>Script as printed by 7-Zip (NsisIn.cpp) for an NSIS uninstaller.</summary>
internal static class NsisSample
{
    public const string Script = """
        Function .onInit
          SetShellVarContext all
        FunctionEnd

        Section Uninstall ; Section_0
          Delete /REBOOTOK $INSTDIR\uninst.exe
          Delete "$SMPROGRAMS\Acme Nsis\Acme Nsis.lnk"
          RMDir "$SMPROGRAMS\Acme Nsis"
          RMDir /r /REBOOTOK $INSTDIR
          Delete $SYSDIR\acmensis.dll
          Delete $APPDATA\AcmeNsis\license.dat
          Delete $LOCALAPPDATA\AcmeNsis\cache.db
          SetShellVarContext current
          RMDir /r $APPDATA\AcmeNsis
          Delete $TEMP\acme.tmp
          Delete $0\file.txt
          SetRegView 64
          DeleteRegKey HKLM Software\AcmeNsis
          DeleteRegKey /ifempty SHCTX Software\AcmeVendor
          DeleteRegValue HKCU Software\Microsoft\Windows\CurrentVersion\Run AcmeNsis
          SetRegView 32
          DeleteRegValue HKLM "Software\Acme Nsis" "Install Dir"
          DeleteRegKey HKCR .acmn
          DeleteRegKey HKLM Software\$R0
        SectionEnd
        """;
}

public class NsisScriptTests
{
    [Fact]
    public void ParsesTheRemovalCommands()
    {
        var actions = NsisScript.Parse(NsisSample.Script.Replace("\n", "\r\n"));

        Assert.Equal(16, actions.Count);
        Assert.Equal(new NsisAction(NsisActionKind.DeleteFile, @"$INSTDIR\uninst.exe") { AllUsers = true }, actions[0]);
        Assert.Equal(@"$SMPROGRAMS\Acme Nsis\Acme Nsis.lnk", actions[1].Target);
        Assert.False(actions[2].Recursive);
        Assert.True(actions[3].Recursive);
        Assert.False(actions[7].AllUsers);
        var key = actions[10];
        Assert.Equal((NsisActionKind.DeleteRegistryKey, "HKLM", @"Software\AcmeNsis", true), (key.Kind, key.Root, key.Target, key.RegView64));
        Assert.True(actions[11].IfEmpty);
        Assert.Equal(("HKCU", "AcmeNsis"), (actions[12].Root, actions[12].ValueName));
        Assert.Equal((@"Software\Acme Nsis", "Install Dir", false), (actions[13].Target, actions[13].ValueName, actions[13].RegView64));
    }

    /// <summary>Excerpt of the script decompiled by the 7-Zip of tools/build-7zip.sh from the Notepad++ 8.9.9 uninstaller.</summary>
    [Fact]
    public void ReadsTheRealOutputOf7Zip()
    {
        const string excerpt = "; NSIS script NSIS-3 Unicode (Uninstall) BadCmd=11\r\n" + """
              RMDir $INSTDIR\localization\
              RMDir $APPDATA\Notepad++\plugins\
              RMDir $_11_\themes\
              SetRegView 64
              DeleteRegKey HKCR *\shell\ANotepad++64
              DeleteRegKey HKCR CLSID\{B298D29A-A6ED-11DE-BA8C-A68E55D89593}
              DeleteRegValue HKCR $2 Notepad++_backup
              DeleteRegKey HKLM "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\notepad++.exe"
              Delete $SMPROGRAMS\Notepad++\Uninstall.lnk
              SetShellVarContext all
              Delete $DESKTOP\Notepad++.lnk
            """;

        var actions = NsisScript.Parse(excerpt);

        Assert.Equal(9, actions.Count);
        Assert.Equal(@"$_11_\themes\", actions[2].Target);
        Assert.Equal(("HKCR", @"*\shell\ANotepad++64", true), (actions[3].Root, actions[3].Target, actions[3].RegView64));
        Assert.Equal(("$2", "Notepad++_backup"), (actions[5].Target, actions[5].ValueName));
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\notepad++.exe", actions[6].Target);
        Assert.False(actions[7].AllUsers);
        Assert.True(actions[8].AllUsers);
    }

    [Fact]
    public void CommentsAndEscapes()
    {
        var actions = NsisScript.Parse("""
            ; Delete C:\commented.txt
            Delete "$INSTDIR\a $\"b$\".txt" ; trailing comment
            Delete $INSTDIR\cost$$.txt
            """);

        Assert.Equal([@"$INSTDIR\a $\""b$\"".txt", @"$INSTDIR\cost$$.txt"], actions.Select(a => a.Target));
    }
}

public class NsisCollectorTests
{
    private const string InstallDir = @"C:\Program Files (x86)\Acme Nsis";

    private sealed class FakeNsisReader(string script) : INsisScriptReader
    {
        public string? Read(string executablePath) => executablePath.EndsWith("uninst.exe", StringComparison.OrdinalIgnoreCase) ? script : null;
    }

    [Fact]
    public void CollectsWhatTheUninstallerRemoves()
    {
        var machine = new TestMachine();
        machine.Files
            .Add($@"{InstallDir}\uninst.exe").Add($@"{InstallDir}\acme.exe")
            .Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Nsis\Acme Nsis.lnk")
            .Add(@"C:\Windows\SysWOW64\acmensis.dll")
            .Add(@"C:\ProgramData\AcmeNsis\license.dat")
            .Add(@"C:\Users\bob\AppData\Local\AcmeNsis\cache.db")
            .Add(@"C:\Users\bob\AppData\Roaming\AcmeNsis\settings.ini");
        // Only existing keys are collected.
        machine.Registry
            .Set(RegHive.LocalMachine, @"SOFTWARE\AcmeNsis")
            .Set(RegHive.Users, $@"{Bob}\Software\AcmeVendor")
            .Set(RegHive.Users, $@"{Bob}\Software\Microsoft\Windows\CurrentVersion\Run", RegValue.String("AcmeNsis", "x"))
            .Set(RegHive.LocalMachine, RegView.Registry32, @"SOFTWARE\Acme Nsis", RegValue.String("Install Dir", InstallDir))
            .Set(RegHive.LocalMachine, RegView.Registry32, @"SOFTWARE\Classes\.acmn");
        var app = new InstalledApp
        {
            Id = @"HKLM32\AcmeNsis", KeyName = "AcmeNsis", RegistryPath = "AcmeNsis", Scope = AppScope.Machine, View = RegView.Registry32,
            DisplayName = "Zeta Nsis", InstallLocation = InstallDir, UninstallString = $"\"{InstallDir}\\uninst.exe\"",
            RawValues = new Dictionary<string, RegValue>(),
            Installer = new InstallerInfo { Type = InstallerType.Nsis, Confidence = 80, Evidence = [], UninstallerPath = $@"{InstallDir}\uninst.exe" },
        };

        using var session = machine.OpenSession();
        var items = new Collectors.CollectionService(machine.Registry, machine.Files, machine.Paths,
                NullLogger<Collectors.CollectionService>.Instance, nsisScripts: new FakeNsisReader(NsisSample.Script))
            .Collect(app, session).Items.Where(i => i.Collector == "NSIS script").ToList();
        CollectedItem Find(string location) => Assert.Single(items, i => i.Location.Equals(location, StringComparison.OrdinalIgnoreCase));

        // The Start menu folder (RMDir without /r: probable) does not absorb its shortcut (certain).
        Assert.Equal((ItemRole.Shortcut, Confidence.Certain),
            (Find(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Nsis\Acme Nsis.lnk").Role,
             Find(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Nsis\Acme Nsis.lnk").Confidence));
        Assert.Equal(Confidence.Probable, Find(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Acme Nsis\").Confidence);
        Assert.Equal(Confidence.Possible, Find(@"C:\Windows\SysWOW64\acmensis.dll").Confidence);
        Assert.Equal(Confidence.Certain, Find(@"C:\ProgramData\AcmeNsis\license.dat").Confidence);
        // Read in the "all users" context, but only the user's copy exists: the other context is tried.
        Find(@"C:\Users\bob\AppData\Local\AcmeNsis\cache.db");
        Assert.Equal(Confidence.Certain, Find(@"C:\Users\bob\AppData\Roaming\AcmeNsis\").Confidence);
        Assert.Equal(Confidence.Certain, Find(@"HKLM64\Software\AcmeNsis").Confidence);
        Assert.Equal(Confidence.Possible, Find($@"HKU\{Bob}\Software\AcmeVendor").Confidence);
        Find($@"HKU\{Bob}\Software\Microsoft\Windows\CurrentVersion\Run → AcmeNsis");
        Find(@"HKLM32\Software\Acme Nsis → Install Dir");
        // HKCR: the machine classes exist (an elevated uninstaller writes them).
        Find(@"HKLM32\SOFTWARE\Classes\.acmn");
        // Install folder (merged elsewhere), $TEMP, $0 and $R0 are not collected here.
        Assert.Equal(11, items.Count);
    }

    [Fact]
    public void MissingKeysAreNotCollected()
    {
        var machine = new TestMachine();
        machine.Files.Add($@"{InstallDir}\uninst.exe");
        var app = new InstalledApp
        {
            Id = "x", KeyName = "x", RegistryPath = "x", Scope = AppScope.Machine, View = RegView.Registry32, DisplayName = "Zeta Nsis",
            InstallLocation = InstallDir, RawValues = new Dictionary<string, RegValue>(),
            Installer = new InstallerInfo { Type = InstallerType.Nsis, Confidence = 80, Evidence = [], UninstallerPath = $@"{InstallDir}\uninst.exe" },
        };

        using var session = machine.OpenSession();
        var items = new Collectors.CollectionService(machine.Registry, machine.Files, machine.Paths,
                NullLogger<Collectors.CollectionService>.Instance, nsisScripts: new FakeNsisReader(NsisSample.Script))
            .Collect(app, session).Items.Where(i => i.Collector == "NSIS script").ToList();

        Assert.DoesNotContain(items, i => i.Registry is not null);
    }
}

public class InstallerLogTests
{
    private const string SetupDir = @"C:\Program Files (x86)\InstallShield Installation Information\{12345678-1234-1234-1234-123456789012}";
    private const string InstallDir = @"C:\Program Files (x86)\Acme IS";
    private const string TempFile = @"C:\Users\bob\AppData\Local\Temp\3ded88ce-efb8-4e72-9d7e-7f8d4ab60266.tmp";
    private const string StartMenuFolder = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Accessibility";

    [Fact]
    public void ExtractsPathsFromBinaryData()
    {
        var data = new List<byte> { 0x01, 0x00, 0xFF };
        data.AddRange(Encoding.Unicode.GetBytes(@"C:\Program Files\Acme\a.dll"));
        data.AddRange([0x00, 0x00, 0x07]);
        data.AddRange(Encoding.ASCII.GetBytes(@"File Copy: D:\Data\Acme\b.txt"));
        data.Add(0x00);

        Assert.Equal([@"C:\Program Files\Acme\a.dll", @"D:\Data\Acme\b.txt"], PathStrings.Extract(data.ToArray()).Order());
    }

    [Fact]
    public void CollectsExistingPathsListedInInstallShieldAndTextLogs()
    {
        var log = new List<byte>();
        foreach (var path in new[] { $@"{InstallDir}\acme.exe", @"C:\Windows\SysWOW64\acmeis.dll", @"C:\ProgramData\IsData\data.db",
                     @"C:\ProgramData\IsData\missing.db", @"C:\Program Files (x86)", StartMenuFolder, @"C:\Users\Public\Desktop\Acme IS.lnk", TempFile,
                     @"C:\Users\bob\OneDrive", @"C:\ProgramData\Zeta\Cache" })
        {
            log.AddRange(Encoding.Unicode.GetBytes(path));
            log.AddRange([0, 0, 0x2A, 0]);
        }
        var machine = new TestMachine();
        machine.Files
            .Add($@"{SetupDir}\setup.exe").Add($@"{SetupDir}\setup.ilg", log.ToArray())
            .Add($@"{InstallDir}\acme.exe").Add($@"{InstallDir}\install.log", "File Copy: C:\\Users\\Public\\Documents\\AcmeIS\\readme.txt\r\n")
            .Add(@"C:\Windows\SysWOW64\acmeis.dll").Add(@"C:\ProgramData\IsData\data.db")
            .Add(@"C:\Users\Public\Documents\AcmeIS\readme.txt")
            .Add($@"{StartMenuFolder}\desktop.ini").Add(@"C:\Users\Public\Desktop\Acme IS.lnk").Add(TempFile)
            .Add(@"C:\Users\bob\OneDrive\desktop.ini").Add(@"C:\ProgramData\Zeta\Cache\index.dat");
        var app = new InstalledApp
        {
            Id = @"HKLM32\{12345678-1234-1234-1234-123456789012}", KeyName = "{12345678-1234-1234-1234-123456789012}", RegistryPath = "x",
            Scope = AppScope.Machine, View = RegView.Registry32, DisplayName = "Zeta IS", InstallLocation = InstallDir,
            RawValues = new Dictionary<string, RegValue>(),
            Installer = new InstallerInfo { Type = InstallerType.InstallShield, Confidence = 90, Evidence = [], UninstallerPath = $@"{SetupDir}\setup.exe" },
        };

        using var session = machine.OpenSession();
        var items = machine.CollectionService().Collect(app, session).Items.Where(i => i.Collector == "Installer log").ToList();

        // Not the Start menu folder, the shortcut (shared shell folders), the temporary file nor OneDrive (not named after the app).
        Assert.Equal(4, items.Count);
        Assert.Contains(items, i => i.Path == @"C:\ProgramData\Zeta\Cache" && i.Kind == ItemKind.Directory);
        Assert.Equal(Confidence.Possible, items.Single(i => i.Path == @"C:\Windows\SysWOW64\acmeis.dll").Confidence);
        Assert.Contains("setup.ilg", items.Single(i => i.Path == @"C:\ProgramData\IsData\data.db").Reason);
        Assert.Contains("install.log", items.Single(i => i.Path == @"C:\Users\Public\Documents\AcmeIS\readme.txt").Reason);
    }
}

public class UsageTraceTests
{
    private const string Other = "S-1-5-21-7-7-7-1001";

    [Fact]
    public void KeepsTheFoldersAndKeysTheApplicationUsed()
    {
        var machine = new TestMachine().WithAcmeEditor();
        machine.Files
            .Add(@"C:\Users\bob\AppData\Roaming\AcmeTrace\settings.json")
            .Add(@"C:\Users\bob\AppData\Local\Temp\acme.tmp")
            .Add(@"C:\ProgramData\AcmeShared\license.dat")
            .Add(@"C:\Users\bob\Documents\report.acme");
        var trace = new UsageTrace
        {
            AppId = machine.App.Id, StartedUtc = DateTime.UtcNow, StoppedUtc = DateTime.UtcNow,
            Processes = [$@"""{InstallDir}\editor.exe"""],
            FilesWritten =
            [
                @"C:\Users\bob\AppData\Roaming\AcmeTrace\settings.json",
                @"C:\Users\bob\AppData\Local\Temp\acme.tmp",
                @"C:\Users\bob\AppData\Roaming\Microsoft\Windows\Recent\report.lnk",
                $@"{InstallDir}\editor.log",
                @"C:\Windows\Prefetch\EDITOR.EXE.pf",
                @"C:\Users\bob\Documents\report.acme",
            ],
            FilesRead = [@"C:\ProgramData\AcmeShared\license.dat"],
            RegistryKeysWritten =
            [
                $@"\REGISTRY\USER\{Bob}\Software\AcmeTrace\Prefs\Window",
                @"\REGISTRY\MACHINE\SOFTWARE\WOW6432Node\AcmeTrace\License",
                $@"\REGISTRY\USER\{Bob}\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
                $@"\REGISTRY\USER\{Other}\Software\AcmeTrace",
                @"\REGISTRY\MACHINE\SYSTEM\CurrentControlSet\Services\bam",
            ],
        };

        using var session = machine.OpenSession();
        var items = machine.CollectionService().Collect(machine.App, session, trace).Items.Where(i => i.Collector == "Usage trace").ToList();
        CollectedItem Find(string location) => Assert.Single(items, i => i.Location.Equals(location, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(Confidence.Probable, Find(@"C:\Users\bob\AppData\Roaming\AcmeTrace\").Confidence);
        Assert.Equal(Confidence.Possible, Find(@"C:\ProgramData\AcmeShared\").Confidence);
        Assert.Equal((ItemKind.File, Confidence.Possible), (Find(@"C:\Users\bob\Documents\report.acme").Kind, Find(@"C:\Users\bob\Documents\report.acme").Confidence));
        Assert.Equal(Confidence.Probable, Find($@"HKU\{Bob}\Software\AcmeTrace\Prefs").Confidence);
        Find(@"HKLM32\SOFTWARE\AcmeTrace\License");
        Assert.Equal(5, items.Count);
    }
}
