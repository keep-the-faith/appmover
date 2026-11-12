using System.Text.RegularExpressions;
using AppMover.Core.Models;
using AppMover.Core.Text;
using static AppMover.Detection.DetectionContext;

namespace AppMover.Detection.Rules;

/// <summary>Inno Setup: uninstaller unins000.exe + log unins000.dat.</summary>
public sealed partial class InnoSetupRule : IDetectionRule
{
    public InstallerType Type => InstallerType.InnoSetup;

    public DetectionCandidate? Evaluate(InstalledApp app, DetectionContext context)
    {
        var candidate = new DetectionCandidate(Type);

        if (app.KeyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase))
            candidate.Add("Uninstall key name ends with _is1", 40);
        if (app.RawValues.TryGetValue("Inno Setup: App Path", out var appPath))
        {
            candidate.Add("'Inno Setup: App Path' value present", 40)
                .Set(InstallerProperties.InstallerAppPath, appPath.AsString());
        }
        if (app.RawValues.ContainsKey("Inno Setup: Setup Version"))
            candidate.Add("'Inno Setup: Setup Version' value present", 20);

        if (context.GetUninstallerPath(app) is { } uninstaller && UninstallerName().IsMatch(WinPath.GetFileName(uninstaller)))
        {
            candidate.Add("Uninstaller is named uninsNNN.exe", 15);
            var log = WinPath.ChangeExtension(uninstaller, ".dat");
            if (context.Files.FileExists(log))
            {
                candidate.Add("Uninstall log found", 20)
                    .Set(InstallerProperties.UninstallLog, log);
            }
        }

        if (context.GetUninstallerMarkers(app).Contains(Markers.InnoSetup))
            candidate.Add("'Inno Setup' signature in the uninstaller", 20);

        return candidate.NullIfEmpty();
    }

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallerName();
}

/// <summary>Nullsoft Scriptable Install System.</summary>
public sealed class NsisRule : IDetectionRule
{
    private static readonly string[] CommonNames = ["uninst.exe", "uninstall.exe", "uninstaller.exe"];

    public InstallerType Type => InstallerType.Nsis;

    public DetectionCandidate? Evaluate(InstalledApp app, DetectionContext context)
    {
        var candidate = new DetectionCandidate(Type);
        var markers = context.GetUninstallerMarkers(app);

        if (markers.Contains(Markers.NsisOverlay))
            candidate.Add("NSIS data signature (NullsoftInst) in the uninstaller", 70);
        else if (markers.Contains(Markers.NsisManifest))
            candidate.Add("NSIS manifest (Nullsoft.NSIS.exehead) in the uninstaller", 50);

        if (context.GetUninstallerPath(app) is { } uninstaller && CommonNames.Any(n => WinPath.FileNameEquals(uninstaller, n)))
            candidate.Add("Uninstaller has a typical NSIS name", 10);

        return candidate.NullIfEmpty();
    }
}

/// <summary>InstallShield (script-based, or wrapping an MSI).</summary>
public sealed class InstallShieldRule : IDetectionRule
{
    public InstallerType Type => InstallerType.InstallShield;

    public DetectionCandidate? Evaluate(InstalledApp app, DetectionContext context)
    {
        var candidate = new DetectionCandidate(Type);

        if (AnyCommandLineContains(app, "InstallShield Installation Information"))
            candidate.Add("Uninstaller in 'InstallShield Installation Information'", 60);
        if (AnyCommandLineContains(app, "-runfromtemp") || AnyCommandLineContains(app, "-removeonly"))
            candidate.Add("InstallShield command line switches", 30);
        if (app.RawValues.TryGetValue("LogFile", out var log) && log.AsString()?.EndsWith(".ilg", StringComparison.OrdinalIgnoreCase) == true)
            candidate.Add("InstallShield log file (.ilg)", 30);
        if (context.GetUninstallerMarkers(app).Contains(Markers.InstallShield))
            candidate.Add("'InstallShield' signature in the uninstaller", 30);

        return candidate.NullIfEmpty();
    }
}

/// <summary>Squirrel.Windows (Electron apps installed in %LOCALAPPDATA%).</summary>
public sealed class SquirrelRule : IDetectionRule
{
    public InstallerType Type => InstallerType.Squirrel;

    public DetectionCandidate? Evaluate(InstalledApp app, DetectionContext context)
    {
        var candidate = new DetectionCandidate(Type);
        if (context.GetUninstallerPath(app) is { } uninstaller
            && WinPath.FileNameEquals(uninstaller, "Update.exe")
            && AnyCommandLineContains(app, "--uninstall"))
        {
            candidate.Add("Uninstall command is 'Update.exe --uninstall'", 60);
            if (context.GetUninstallerMarkers(app).Contains(Markers.Squirrel))
                candidate.Add("'Squirrel' signature in Update.exe", 30);
        }
        return candidate.NullIfEmpty();
    }
}

/// <summary>ClickOnce (managed by dfshim, not transferable).</summary>
public sealed class ClickOnceRule : IDetectionRule
{
    public InstallerType Type => InstallerType.ClickOnce;

    public DetectionCandidate? Evaluate(InstalledApp app, DetectionContext context)
    {
        var candidate = new DetectionCandidate(Type);
        if (AnyCommandLineContains(app, "dfshim"))
            candidate.Add("Uninstall command uses dfshim (ClickOnce)", 90);
        return candidate.NullIfEmpty();
    }
}
