using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace AppMover.Deployment.Windows;

/// <summary>Creates .lnk files with the shell's IShellLink.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShortcutWriter : IShortcutWriter
{
    public void Create(string path, ShortcutTarget shortcut)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(shortcut.Target);
            if (shortcut.Arguments is not null)
                link.SetArguments(shortcut.Arguments);
            if (shortcut.WorkingDirectory is not null)
                link.SetWorkingDirectory(shortcut.WorkingDirectory);
            if (shortcut.Description is not null)
                link.SetDescription(shortcut.Description);
            if (shortcut.IconLocation is not null)
            {
                // "C:\App\app.exe,1" → path + index.
                var comma = shortcut.IconLocation.LastIndexOf(',');
                if (comma > 0 && int.TryParse(shortcut.IconLocation[(comma + 1)..], out var index))
                    link.SetIconLocation(shortcut.IconLocation[..comma], index);
                else
                    link.SetIconLocation(shortcut.IconLocation, 0);
            }
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    /// <summary>Vtable order matters: every method up to SetPath is declared.</summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}

/// <summary>Broadcasts environment and file association changes (Explorer, new processes).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemNotifier : ISystemNotifier
{
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int ShcneAssocChanged = 0x08000000;

    public void EnvironmentChanged() =>
        SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment", SmtoAbortIfHung, 5000, out _);

    public void AssociationsChanged() => SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
