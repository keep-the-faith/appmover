using AppMover.Core.Msi;
using AppMover.Core.Text;

namespace AppMover.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\uninst.exe\" /S", @"C:\Program Files\App\uninst.exe")]
    [InlineData(@"C:\Program Files\App\uninst.exe /S", @"C:\Program Files\App\uninst.exe")]
    [InlineData(@"C:\Program Files\App\uninst.exe", @"C:\Program Files\App\uninst.exe")]
    [InlineData("MsiExec.exe /X{23170F69-40C1-2702-1900-000001000000}", "MsiExec.exe")]
    [InlineData("rundll32.exe dfshim.dll,ShArpMaintain App.application", "rundll32.exe")]
    [InlineData(@"C:\Users\bob\AppData\Local\App\Update.exe --uninstall", @"C:\Users\bob\AppData\Local\App\Update.exe")]
    [InlineData(@"%ProgramFiles%\My.exe.Tool\run.exe /x", @"%ProgramFiles%\My.exe.Tool\run.exe")]
    [InlineData(@"C:\Tools\uninstall.cmd /q", @"C:\Tools\uninstall.cmd")]
    [InlineData("\"C:\\App\\unins000.exe", @"C:\App\unins000.exe")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void ExtractExecutable(string? commandLine, string? expected) =>
        Assert.Equal(expected, CommandLine.ExtractExecutable(commandLine));
}


public class MsiGuidTests
{
    private const string ProductCode = "{23170F69-40C1-2702-1900-000001000000}";
    private const string Packed = "96F071321C0420729100000010000000";

    [Fact]
    public void Pack() => Assert.Equal(Packed, MsiGuid.Pack(ProductCode));

    [Fact]
    public void PackIsCaseInsensitive() => Assert.Equal(Packed, MsiGuid.Pack(ProductCode.ToLowerInvariant()));

    [Theory]
    [InlineData("not a guid")]
    [InlineData("")]
    public void PackInvalid(string value) => Assert.Null(MsiGuid.Pack(value));

    [Theory]
    [InlineData(ProductCode, true)]
    [InlineData("23170F69-40C1-2702-1900-000001000000", false)]
    [InlineData("7-Zip", false)]
    [InlineData(null, false)]
    public void IsBracedGuid(string? value, bool expected) => Assert.Equal(expected, MsiGuid.IsBracedGuid(value));
}
