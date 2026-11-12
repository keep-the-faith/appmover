namespace AppMover.Core.Registry;

/// <summary>Write access to a registry (real Windows registry, or an in-memory one for tests).</summary>
public interface IRegistryWriter
{
    /// <summary>Creates the key and its missing parents (no-op when it exists).</summary>
    void CreateKey(RegHive hive, RegView view, string path);

    /// <summary>Creates or replaces a value; the key must exist.</summary>
    void SetValue(RegHive hive, RegView view, string path, RegValue value);

    /// <summary>Deletes a value (no-op when it does not exist).</summary>
    void DeleteValue(RegHive hive, RegView view, string path, string name);

    /// <summary>Deletes a key and all its subkeys (no-op when it does not exist).</summary>
    void DeleteKeyTree(RegHive hive, RegView view, string path);
}
