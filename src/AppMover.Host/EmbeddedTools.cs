using System.Security.Cryptography;

namespace AppMover.Host;

/// <summary>
/// Native tools embedded in AppMover (7-Zip, KernelTraceControl.dll): a single-file executable cannot run or load them
/// from inside itself, so they are copied to %ProgramFiles%\AppMover\tools (writable by administrators only), and their
/// SHA-256 is checked before every use so that this elevated process never runs a modified copy.
/// </summary>
internal static class EmbeddedTools
{
    /// <summary>Extracts a resource of this assembly and returns the full path of the checked copy.</summary>
    /// <param name="resourceName">Logical name of the embedded resource.</param>
    /// <param name="relativePath">Path of the copy under the tools folder, from the first 12 hex digits of its hash.</param>
    public static string Extract(string resourceName, Func<string, string> relativePath)
    {
        using var resource = typeof(EmbeddedTools).Assembly.GetManifestResourceStream(resourceName)
                             ?? throw new InvalidDataException($"{resourceName} is not embedded in this build of AppMover.");
        var bytes = new byte[resource.Length];
        resource.ReadExactly(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        var tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AppMover", "tools");
        var path = Path.Combine(tools, relativePath(hash[..12].ToLowerInvariant()));
        if (!File.Exists(path) || !HashEquals(path, hash))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            if (!HashEquals(path, hash))
                throw new InvalidDataException($"{path} does not match the embedded {resourceName}.");
        }
        return path;
    }

    private static bool HashEquals(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)) == expected;
    }
}
