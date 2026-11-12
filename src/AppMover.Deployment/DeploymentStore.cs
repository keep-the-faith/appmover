using System.Text.Json;
using AppMover.Core.Platform;
using AppMover.Core.Text;
using AppMover.Packaging;

namespace AppMover.Deployment;

/// <summary>Keeps the deployment journals (and backups of replaced files), by default in %ProgramData%\AppMover\Deployments.</summary>
public sealed class DeploymentStore(IWritableFileSystem files, string root)
{
    private const string RecordFileName = "deployment.json";

    public string GetDirectory(string id) => WinPath.Combine(root, id);

    public string GetBackupDirectory(string id) => WinPath.Combine(GetDirectory(id), "backup");

    public void Save(DeploymentRecord record)
    {
        var directory = GetDirectory(record.Id);
        files.CreateDirectory(directory);
        using var stream = files.Create(WinPath.Combine(directory, RecordFileName));
        JsonSerializer.Serialize(stream, record, PackageManifest.JsonOptions);
    }

    public DeploymentRecord? Load(string id)
    {
        var path = WinPath.Combine(GetDirectory(id), RecordFileName);
        if (!files.FileExists(path))
            return null;
        using var stream = files.OpenRead(path);
        return JsonSerializer.Deserialize<DeploymentRecord>(stream, PackageManifest.JsonOptions);
    }

    public IReadOnlyList<DeploymentRecord> List() =>
        files.EnumerateDirectories(root)
            .Select(d => Load(WinPath.GetFileName(d)))
            .OfType<DeploymentRecord>()
            .OrderBy(r => r.DeployedUtc)
            .ToList();

    /// <summary>Removes the journal and the backups of a deployment.</summary>
    public void Delete(string id)
    {
        var directory = GetDirectory(id);
        foreach (var file in files.EnumerateFiles(directory, recursive: true).ToList())
            files.DeleteFile(file);
        foreach (var sub in AllDirectories(directory).OrderByDescending(d => d.Length).ToList())
            files.DeleteDirectory(sub);
        files.DeleteDirectory(directory);
    }

    /// <summary>A new id: readable and unique ("AcmeEditor-20261111-174500-3f2a").</summary>
    public static string NewId(string appKey)
    {
        var name = new string(appKey.Where(char.IsAsciiLetterOrDigit).Take(40).ToArray());
        return $"{(name.Length == 0 ? "app" : name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
    }

    private IEnumerable<string> AllDirectories(string directory)
    {
        foreach (var sub in files.EnumerateDirectories(directory))
        {
            yield return sub;
            foreach (var nested in AllDirectories(sub))
                yield return nested;
        }
    }
}
