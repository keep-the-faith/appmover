using AppMover.Core.Text;

namespace AppMover.Core.Platform;

public static class FileSystemExtensions
{
    /// <summary>
    /// Folder path with the case of the folders on the disk: "c:\gestcime" (as written by an installer in the registry)
    /// → "C:\GESTCIME". Parts that do not exist, and paths that are not on a drive, are returned as given (normalized).
    /// </summary>
    public static string GetActualDirectoryCase(this IFileSystem files, string path)
    {
        var normalized = WinPath.Normalize(path);
        if (normalized.Length < 3 || normalized[1] != ':' || normalized[2] != '\\')
            return normalized;

        var current = char.ToUpperInvariant(normalized[0]) + @":\";
        var segments = normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var match = files.EnumerateDirectories(current).FirstOrDefault(d => WinPath.FileNameEquals(d, segments[i]));
            if (match is null)
                return WinPath.Combine(current, string.Join('\\', segments[i..]));
            current = WinPath.Combine(current, WinPath.GetFileName(match));
        }
        return current;
    }
}
