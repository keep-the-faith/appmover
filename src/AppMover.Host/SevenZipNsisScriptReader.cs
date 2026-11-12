using System.Diagnostics;
using AppMover.Core.Nsis;
using AppMover.Core.Text;
using Microsoft.Extensions.Logging;

namespace AppMover.Host;

/// <summary>
/// Extracts "[NSIS].nsi" (the script decompiled by 7-Zip) from an NSIS executable, with the 7-Zip embedded in AppMover:
/// official 7-Zip builds do not decompile NSIS scripts, ours is built with NSIS_SCRIPT (see third_party/7-Zip/README.txt).
/// APPMOVER_7ZIP may point to another 7-Zip built the same way.
/// </summary>
public sealed class SevenZipNsisScriptReader(ILogger logger) : INsisScriptReader
{
    private const string ScriptName = "[NSIS].nsi";
    private const string ResourceName = "AppMover.Tools.7zz.exe";

    public string? Read(string executablePath)
    {
        string sevenZip;
        try
        {
            sevenZip = Environment.GetEnvironmentVariable("APPMOVER_7ZIP") is { Length: > 0 } custom ? custom : EmbeddedTools.Extract(ResourceName, hash => $"7zz-{hash}.exe");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(e, "The embedded 7-Zip cannot be used: NSIS uninstallers are not analyzed");
            return null;
        }

        var info = new ProcessStartInfo(sevenZip)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "e", "-so", "-y", executablePath, ScriptName })
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info)!;
            using var output = new MemoryStream();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.BaseStream.CopyTo(output);
            if (!process.WaitForExit(60_000))
            {
                process.Kill();
                return null;
            }
            if (process.ExitCode != 0 || output.Length == 0)
            {
                logger.LogDebug("No NSIS script in {Path}: {Error}", executablePath, error.Result.Trim());
                return null;
            }
            return TextCodec.Decode(output.ToArray())?.Text;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Cannot extract the NSIS script of {Path}", executablePath);
            return null;
        }
    }
}
