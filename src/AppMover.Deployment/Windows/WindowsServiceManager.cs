using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AppMover.Deployment.Windows;

/// <summary>Creates and deletes services through the Service Control Manager.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceManager : IServiceManager
{
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceControlStop = 0x1;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceDoesNotExist = 1060;

    public bool Exists(string name)
    {
        using var scm = OpenManager();
        var service = OpenService(scm.Handle, name, ServiceQueryStatus);
        if (service == IntPtr.Zero)
            return Marshal.GetLastWin32Error() != ErrorServiceDoesNotExist;
        CloseServiceHandle(service);
        return true;
    }

    public void Create(ServiceSpec spec)
    {
        using var scm = OpenManager();
        // Dependencies: double null-terminated list.
        var dependencies = spec.Dependencies.Length == 0 ? null : string.Join('\0', spec.Dependencies) + "\0\0";
        var service = CreateService(scm.Handle, spec.Name, spec.DisplayName ?? spec.Name, ServiceAllAccess, spec.Type, spec.Start,
            spec.ErrorControl, spec.ImagePath, null, IntPtr.Zero, dependencies, spec.Account, null);
        if (service == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot create service {spec.Name}");
        CloseServiceHandle(service);
    }

    public void Start(string name)
    {
        using var scm = OpenManager();
        var service = OpenService(scm.Handle, name, ServiceStart);
        if (service == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open service {name}");
        try
        {
            if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() is var error && error != ErrorServiceAlreadyRunning)
                throw new Win32Exception(error, $"Cannot start service {name}");
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    public void Delete(string name)
    {
        using var scm = OpenManager();
        var service = OpenService(scm.Handle, name, ServiceAllAccess);
        if (service == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
                return;
            throw new Win32Exception(error, $"Cannot open service {name}");
        }
        try
        {
            ControlService(service, ServiceControlStop, out _); // fails harmlessly when already stopped
            if (!DeleteService(service))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot delete service {name}");
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    private static ManagerHandle OpenManager()
    {
        var handle = OpenSCManager(null, null, ScManagerAllAccess);
        return handle == IntPtr.Zero ? throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the service manager") : new ManagerHandle(handle);
    }

    private sealed class ManagerHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle => handle;
        public void Dispose() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(IntPtr scManager, string serviceName, string displayName, uint desiredAccess, uint serviceType,
        uint startType, uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr service, uint numServiceArgs, IntPtr serviceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
