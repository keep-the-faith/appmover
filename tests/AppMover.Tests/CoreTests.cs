using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Platform;
using AppMover.Core.Registry;
using AppMover.Core.Shell;
using AppMover.Core.Text;
using AppMover.Tests.Fakes;

namespace AppMover.Tests;

public class ShellLinkTests
{
    [Fact]
    public void ReadsTargetAndArguments()
    {
        var link = ShellLink.Parse(ShellLinkBuilder.Target(@"C:\Program Files\Acme\editor.exe", "--new"));

        Assert.NotNull(link);
        Assert.Equal(@"C:\Program Files\Acme\editor.exe", link.Target);
        Assert.Equal("--new", link.Arguments);
        Assert.Null(link.DarwinId);
    }

    [Fact]
    public void ReadsAdvertisedShortcut()
    {
        var link = ShellLink.Parse(ShellLinkBuilder.Advertised("w_]b0abc", @"C:\Windows\Installer\{GUID}\icon.ico"));

        Assert.NotNull(link);
        Assert.Null(link.Target);
        Assert.Equal("w_]b0abc", link.DarwinId);
        Assert.Equal(@"C:\Windows\Installer\{GUID}\icon.ico", link.IconLocation);
    }

    [Fact]
    public void RejectsInvalidData()
    {
        Assert.Null(ShellLink.Parse("not a shortcut"u8));
        var truncated = ShellLinkBuilder.Target(@"C:\a.exe")[..0x50];
        Assert.Null(ShellLink.Parse(truncated));
    }
}

public class WinPathTests
{
    [Theory]
    [InlineData(@"C:\Program Files\App\unins000.exe", "unins000.exe")]
    [InlineData("C:/App/a.exe", "a.exe")]
    [InlineData("a.exe", "a.exe")]
    public void GetFileName(string path, string expected) => Assert.Equal(expected, WinPath.GetFileName(path));

    [Theory]
    [InlineData(@"C:\Program Files\App\unins000.exe", @"C:\Program Files\App")]
    [InlineData(@"C:\App\", "C:\\")]
    [InlineData("file.txt", null)]
    public void GetDirectoryName(string path, string? expected) => Assert.Equal(expected, WinPath.GetDirectoryName(path));

    [Theory]
    [InlineData(@"C:\App\unins000.exe", ".dat", @"C:\App\unins000.dat")]
    [InlineData(@"C:\My.App\uninstall", ".dat", @"C:\My.App\uninstall.dat")]
    public void ChangeExtension(string path, string extension, string expected) =>
        Assert.Equal(expected, WinPath.ChangeExtension(path, extension));

    [Theory]
    [InlineData(@"C:\App\bin\a.exe", @"C:\App", true)]
    [InlineData(@"C:\App", @"c:\app\", true)]
    [InlineData(@"C:\Apps\a.exe", @"C:\App", false)]
    [InlineData(@"C:\App", @"C:\App\bin", false)]
    [InlineData(@"D:\x", "C:\\", false)]
    [InlineData(@"C:\x", "C:\\", true)]
    public void IsUnderOrEqual(string path, string root, bool expected) => Assert.Equal(expected, WinPath.IsUnderOrEqual(path, root));

    [Theory]
    [InlineData("\"C:\\App\\\"", @"C:\App")]
    [InlineData("C:/App/", @"C:\App")]
    [InlineData(@"\\?\C:\App", @"C:\App")]
    [InlineData("C:", "C:\\")]
    public void Normalize(string path, string expected) => Assert.Equal(expected, WinPath.Normalize(path));

    [Theory]
    [InlineData("C:\\", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"C:\App", false)]
    public void IsRoot(string path, bool expected) => Assert.Equal(expected, WinPath.IsRoot(path));
}

public class PathTokenizerTests
{
    private readonly PathTokenizer _tokenizer = new(
    [
        ("ProgramFiles", @"C:\Program Files"),
        ("U0.AppData", @"C:\Users\bob\AppData\Roaming"),
        ("U0.Profile", @"C:\Users\bob"),
        ("InstallDir", @"C:\Program Files\Acme\Editor"),
        ("Root", "C:\\"),
    ]);

    [Theory]
    [InlineData(@"C:\Program Files\Acme\Editor\editor.exe", @"{InstallDir}\editor.exe")]
    [InlineData(@"C:\Program Files\Acme\Editor", "{InstallDir}")]
    [InlineData(@"C:\Program Files\Other\x.dll", @"{ProgramFiles}\Other\x.dll")]
    [InlineData(@"c:\users\bob\appdata\roaming\Acme\a.ini", @"{U0.AppData}\Acme\a.ini")]
    [InlineData(@"C:\Users\bob\Desktop", @"{U0.Profile}\Desktop")]
    [InlineData(@"C:\Program Files (x86)\a", @"C:\Program Files (x86)\a")]
    [InlineData(@"D:\Games\x", @"D:\Games\x")]
    public void Tokenize(string path, string expected) => Assert.Equal(expected, _tokenizer.Tokenize(path));

    [Fact]
    public void DriveRootsAreNeverTokens() => Assert.DoesNotContain(_tokenizer.Mappings, m => m.Token == "Root");

    [Theory]
    [InlineData("\"C:\\Program Files\\Acme\\Editor\\editor.exe\" \"%1\"", "\"{InstallDir}\\editor.exe\" \"%1\"")]
    [InlineData(@"C:\Program Files\Acme\Editor;C:\Program Files\Acme\Editors", @"{InstallDir};{ProgramFiles}\Acme\Editors")]
    [InlineData("v2.30|App=C:\\Program Files\\Acme\\Editor\\editor.exe|", "v2.30|App={InstallDir}\\editor.exe|")]
    [InlineData("no path here", "no path here")]
    public void TokenizeText(string text, string expected) => Assert.Equal(expected, _tokenizer.TokenizeText(text));

    [Theory]
    [InlineData(@"{InstallDir}\bin\a.exe", "files/InstallDir/bin/a.exe")]
    [InlineData(@"{U0.AppData}\Acme", "files/U0.AppData/Acme")]
    [InlineData(@"d:\Games\x.sav", "files/_drive_D/Games/x.sav")]
    [InlineData(@"\\server\share\x", "files/_unc/server/share/x")]
    public void ToArchivePath(string tokenized, string expected) => Assert.Equal(expected, PathTokenizer.ToArchivePath(tokenized));
}

public class AppKeywordsTests
{
    [Theory]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip")]
    [InlineData("Python 3.12.1 (64-bit)", "Python")]
    [InlineData("Mozilla Firefox (x64 fr)", "Mozilla Firefox")]
    [InlineData("Git version 2.43.0", "Git")]
    [InlineData("VLC media player", "VLC media player")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("Acme Editor 2", "Acme Editor")]
    public void CleanProductName(string name, string expected) => Assert.Equal(expected, AppKeywords.CleanProductName(name));

    [Theory]
    [InlineData("Mozilla Corporation", "Mozilla")]
    [InlineData("Acme Software Inc.", "Acme")]
    [InlineData("Notepad++ Team", "Notepad++")]
    [InlineData("Igor Pavlov", "Igor Pavlov")]
    [InlineData("VideoLAN", "VideoLAN")]
    [InlineData(null, "")]
    public void CleanPublisher(string? publisher, string expected) => Assert.Equal(expected, AppKeywords.CleanPublisher(publisher));

    [Fact]
    public void Match()
    {
        var app = new InstalledApp
        {
            Id = "x", KeyName = "x", RegistryPath = "x", Scope = AppScope.Machine, View = RegView.Registry64,
            DisplayName = "Mozilla Firefox (x64 fr)", Publisher = "Mozilla", RawValues = new Dictionary<string, RegValue>(),
        };
        var keywords = AppKeywords.From(app, [@"C:\Program Files\Mozilla Firefox"], _ => false);

        Assert.Equal(KeywordMatch.Product, keywords.Match("Firefox"));
        Assert.Equal(KeywordMatch.Product, keywords.Match("Mozilla Firefox"));
        Assert.Equal(KeywordMatch.Publisher, keywords.Match("Mozilla"));
        Assert.Equal(KeywordMatch.Partial, keywords.Match("Firefox Developer Edition"));
        Assert.Equal(KeywordMatch.None, keywords.Match("Thunderbird"));
        Assert.Equal(KeywordMatch.None, keywords.Match("Microsoft"));
        Assert.Equal(KeywordMatch.None, keywords.Match("ff"));
    }

    [Fact]
    public void GenericPublisherIsNotSpecific()
    {
        Assert.False(AppKeywords.Empty.IsSpecificPublisher("microsoft"));
        Assert.True(AppKeywords.Empty.IsSpecificPublisher("videolan"));
    }
}

public class FileSystemExtensionsTests
{
    private readonly FakeFileSystem _files = new FakeFileSystem().Add(@"C:\Program Files\GESTCIME\Data\db.fdb");

    [Theory]
    [InlineData(@"c:\program files\gestcime\data", @"C:\Program Files\GESTCIME\Data")]
    [InlineData(@"c:\program files\gestcime\missing\sub", @"C:\Program Files\GESTCIME\missing\sub")] // missing parts kept as given
    [InlineData(@"c:\", @"C:\")]
    [InlineData(@"\\server\share\app", @"\\server\share\app")]
    public void GetActualDirectoryCase(string path, string expected) => Assert.Equal(expected, _files.GetActualDirectoryCase(path));
}
