using AppMover.Core.Registry;

namespace AppMover.Tests.Fakes;

/// <summary>In-memory registry. <see cref="RegView.Default"/> is the 64-bit view.</summary>
public sealed class FakeRegistry : IRegistrySource, IRegistryWriter
{
    private readonly Dictionary<(RegHive, RegView), Node> _roots = [];

    public FakeRegistry Set(RegHive hive, string path, params RegValue[] values) => Set(hive, RegView.Registry64, path, values);

    public FakeRegistry Set(RegHive hive, RegView view, string path, params RegValue[] values)
    {
        var node = Root(hive, view);
        foreach (var part in Split(path))
        {
            if (!node.Children.TryGetValue(part, out var child))
                node.Children[part] = child = new Node(part);
            node = child;
        }
        foreach (var value in values)
            node.Values[value.Name] = value;
        return this;
    }

    public IRegistryKey? OpenKey(RegHive hive, RegView view, string subKeyPath)
    {
        var root = Root(hive, view);
        var node = Find(root, subKeyPath);
        return node is null ? null : new FakeKey(node, $@"{hive}\{subKeyPath}".TrimEnd('\\'));
    }

    public void CreateKey(RegHive hive, RegView view, string path) => Set(hive, view, path);

    /// <summary>Keys whose values cannot be written (like a key owned by TrustedInstaller).</summary>
    public HashSet<string> Protected { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetValue(RegHive hive, RegView view, string path, RegValue value)
    {
        if (Protected.Contains(path))
            throw new UnauthorizedAccessException("Requested registry access is not allowed.");
        var node = Find(Root(hive, view), path) ?? throw new IOException($"Key not found: {path}");
        node.Values[value.Name] = value;
    }

    public void DeleteValue(RegHive hive, RegView view, string path, string name) => Find(Root(hive, view), path)?.Values.Remove(name);

    public void DeleteKeyTree(RegHive hive, RegView view, string path)
    {
        var parts = Split(path);
        var parent = Find(Root(hive, view), string.Join('\\', parts[..^1]));
        parent?.Children.Remove(parts[^1]);
    }

    /// <summary>Every key and value, sorted: compare two dumps to check that a machine is back to its initial state.</summary>
    public List<string> Dump()
    {
        var lines = new List<string>();
        void Walk(Node node, string prefix)
        {
            if (prefix.Contains('\\'))
                lines.Add(prefix); // roots are created on first access: not part of the content
            foreach (var value in node.Values.Values)
                lines.Add($"{prefix} | {value.Name} = {value.Type}:{value.AsString() ?? Convert.ToHexString(value.Binary ?? [])}");
            foreach (var child in node.Children.Values)
                Walk(child, $@"{prefix}\{child.Name}");
        }
        foreach (var ((hive, view), root) in _roots)
            Walk(root, $"{hive}/{view}");
        lines.Sort(StringComparer.OrdinalIgnoreCase);
        return lines;
    }

    private Node Root(RegHive hive, RegView view)
    {
        var key = (hive, view == RegView.Default ? RegView.Registry64 : view);
        if (!_roots.TryGetValue(key, out var root))
            _roots[key] = root = new Node("");
        return root;
    }

    private static Node? Find(Node node, string path)
    {
        foreach (var part in Split(path))
        {
            if (!node.Children.TryGetValue(part, out var child))
                return null;
            node = child;
        }
        return node;
    }

    private static string[] Split(string path) => path.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    private sealed class Node(string name)
    {
        public string Name => name;
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RegValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeKey(Node node, string path) : IRegistryKey
    {
        public string Path => path;
        public IReadOnlyList<string> GetSubKeyNames() => node.Children.Values.Select(c => c.Name).ToList();
        public IReadOnlyList<string> GetValueNames() => node.Values.Keys.ToList();
        public RegValue? GetValue(string name) => node.Values.GetValueOrDefault(name);

        public IRegistryKey? OpenSubKey(string name)
        {
            var child = Find(node, name);
            return child is null ? null : new FakeKey(child, $@"{path}\{name}");
        }

        public void Dispose() { }
    }
}
