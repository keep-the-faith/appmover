namespace AppMover.Core.Registry;

/// <summary>Read-only access to a registry (real Windows registry, or an in-memory one for tests / archives).</summary>
public interface IRegistrySource
{
    /// <summary>Opens a key, or returns null if it does not exist or is not accessible.</summary>
    IRegistryKey? OpenKey(RegHive hive, RegView view, string subKeyPath);
}

public interface IRegistryKey : IDisposable
{
    /// <summary>Full path, e.g. <c>HKEY_LOCAL_MACHINE\SOFTWARE\Vendor</c>.</summary>
    string Path { get; }
    IReadOnlyList<string> GetSubKeyNames();
    IReadOnlyList<string> GetValueNames();
    RegValue? GetValue(string name);
    IRegistryKey? OpenSubKey(string name);
}

public static class RegistryKeyExtensions
{
    public static string? GetString(this IRegistryKey key, string name) => key.GetValue(name)?.AsString();

    public static IReadOnlyDictionary<string, RegValue> GetAllValues(this IRegistryKey key)
    {
        var values = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is { } value)
                values[name] = value;
        }
        return values;
    }
}
