using AppMover.Core.Registry;
using AppMover.Deployment;

namespace AppMover.Tests.Fakes;

/// <summary>Service manager that, like the real one, writes the service key in the registry.</summary>
public sealed class FakeServiceManager(FakeRegistry registry) : IServiceManager
{
    private const string Services = @"SYSTEM\CurrentControlSet\Services";

    public Dictionary<string, ServiceSpec> Created { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Started { get; } = [];
    /// <summary>Creating this service fails (to test the rollback).</summary>
    public string? FailOn { get; set; }
    /// <summary>Starting this service fails.</summary>
    public string? FailToStart { get; set; }

    public bool Exists(string name)
    {
        using var key = registry.OpenKey(RegHive.LocalMachine, RegView.Registry64, $@"{Services}\{name}");
        return key is not null;
    }

    public void Create(ServiceSpec spec)
    {
        if (spec.Name == FailOn)
            throw new InvalidOperationException($"Access denied creating {spec.Name}");
        Created[spec.Name] = spec;
        registry.Set(RegHive.LocalMachine, $@"{Services}\{spec.Name}",
            RegValue.ExpandString("ImagePath", spec.ImagePath), RegValue.DWord("Type", spec.Type),
            RegValue.DWord("Start", spec.Start), RegValue.DWord("ErrorControl", spec.ErrorControl),
            RegValue.String("ObjectName", spec.Account ?? "LocalSystem"));
    }

    public void Start(string name)
    {
        if (name == FailToStart)
            throw new InvalidOperationException($"The service {name} did not respond");
        Started.Add(name);
    }

    public void Delete(string name)
    {
        Created.Remove(name);
        registry.DeleteKeyTree(RegHive.LocalMachine, RegView.Registry64, $@"{Services}\{name}");
    }
}

public sealed class FakeTaskScheduler : ITaskScheduler
{
    public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string name) => Tasks.ContainsKey(name);
    public void Register(string name, string xml) => Tasks[name] = xml;
    public void Delete(string name) => Tasks.Remove(name);
}

/// <summary>Writes "LNK|target|arguments" in the .lnk file.</summary>
public sealed class FakeShortcutWriter(FakeFileSystem files) : IShortcutWriter
{
    public Dictionary<string, ShortcutTarget> Created { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Create(string path, ShortcutTarget shortcut)
    {
        Created[path] = shortcut;
        using var stream = files.Create(path);
        stream.Write(System.Text.Encoding.UTF8.GetBytes($"LNK|{shortcut.Target}|{shortcut.Arguments}"));
    }
}

public sealed class FakeNotifier : ISystemNotifier
{
    public int Environment { get; private set; }
    public int Associations { get; private set; }

    public void EnvironmentChanged() => Environment++;
    public void AssociationsChanged() => Associations++;
}

/// <summary>All the fake system services of a <see cref="TestMachine"/>.</summary>
public sealed class FakeSystem
{
    public FakeSystem(TestMachine machine)
    {
        Services = new FakeServiceManager(machine.Registry);
        Shortcuts = new FakeShortcutWriter(machine.Files);
        Value = new SystemServices(Services, Tasks, Shortcuts, Notifier);
    }

    public FakeServiceManager Services { get; }
    public FakeTaskScheduler Tasks { get; } = new();
    public FakeShortcutWriter Shortcuts { get; }
    public FakeNotifier Notifier { get; } = new();
    public SystemServices Value { get; }
}
