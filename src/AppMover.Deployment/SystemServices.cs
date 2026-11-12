namespace AppMover.Deployment;

/// <summary>Configuration of a Windows service to create (values of its registry key). <c>Account</c> is null for LocalSystem.</summary>
public sealed record ServiceSpec(
    string Name, string ImagePath, uint Type, uint Start, uint ErrorControl, string? DisplayName, string? Account, string[] Dependencies)
{
    private const uint AutoStart = 2;
    private const uint Win32Service = 0x30;

    /// <summary>A program (not a driver) started with Windows: installers start these at the end of the setup.</summary>
    public bool StartsWithWindows => Start == AutoStart && (Type & Win32Service) != 0;
}

/// <summary>Service Control Manager.</summary>
public interface IServiceManager
{
    bool Exists(string name);
    void Create(ServiceSpec spec);
    /// <summary>Starts the service (nothing to do when it is already running).</summary>
    void Start(string name);
    /// <summary>Stops (if running) and deletes the service.</summary>
    void Delete(string name);
}

/// <summary>Task Scheduler.</summary>
public interface ITaskScheduler
{
    bool Exists(string name);
    /// <summary>Registers (or replaces) a task from its XML definition.</summary>
    void Register(string name, string xml);
    void Delete(string name);
}

/// <summary>Target of a shortcut, with paths valid on the target machine.</summary>
public sealed record ShortcutTarget(string Target, string? Arguments, string? WorkingDirectory, string? IconLocation, string? Description);

public interface IShortcutWriter
{
    /// <summary>Creates or replaces a .lnk file.</summary>
    void Create(string path, ShortcutTarget shortcut);
}

/// <summary>Tells running programs (Explorer…) that the configuration changed.</summary>
public interface ISystemNotifier
{
    void EnvironmentChanged();
    void AssociationsChanged();
}

public sealed record SystemServices(IServiceManager Services, ITaskScheduler Tasks, IShortcutWriter Shortcuts, ISystemNotifier Notifier);
