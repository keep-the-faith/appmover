namespace AppMover.Core.Platform;

/// <summary>Expands environment variables in paths (mockable for tests).</summary>
public interface IPathResolver
{
    string Expand(string path);
}

public sealed class SystemPathResolver : IPathResolver
{
    public string Expand(string path) => Environment.ExpandEnvironmentVariables(path);
}
