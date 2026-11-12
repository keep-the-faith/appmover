using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace AppMover.Deployment.Windows;

/// <summary>Registers tasks with schtasks.exe (handles the XML definition exactly as exported).</summary>
[SupportedOSPlatform("windows")]
public sealed class SchtasksScheduler : ITaskScheduler
{
    public bool Exists(string name) => Run("/Query", "/TN", name).ExitCode == 0;

    public void Register(string name, string xml)
    {
        // schtasks expects a UTF-16 file.
        var file = Path.Combine(Path.GetTempPath(), $"appmover-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + xml, Encoding.Unicode);
        try
        {
            var (exitCode, output) = Run("/Create", "/TN", name, "/XML", file, "/F");
            if (exitCode != 0)
                throw new InvalidOperationException($"Cannot register task {name}: {output.Trim()}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    public void Delete(string name)
    {
        var (exitCode, output) = Run("/Delete", "/TN", name, "/F");
        if (exitCode != 0)
            throw new InvalidOperationException($"Cannot delete task {name}: {output.Trim()}");
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start schtasks.exe");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error);
    }
}
