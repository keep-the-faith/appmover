using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace AppMover.Core.Registry;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistrySource : IRegistrySource, IRegistryWriter
{
    public IRegistryKey? OpenKey(RegHive hive, RegView view, string subKeyPath)
    {
        var baseKey = RegistryKey.OpenBaseKey(Map(hive), Map(view));
        if (string.IsNullOrEmpty(subKeyPath))
            return new WindowsRegistryKey(baseKey);

        try
        {
            var key = baseKey.OpenSubKey(subKeyPath, writable: false);
            return key is null ? null : new WindowsRegistryKey(key);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
        finally
        {
            baseKey.Dispose();
        }
    }

    public void CreateKey(RegHive hive, RegView view, string path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(Map(hive), Map(view));
        using var key = baseKey.CreateSubKey(path, writable: false);
    }

    public void SetValue(RegHive hive, RegView view, string path, RegValue value)
    {
        using var key = OpenWritable(hive, view, path);
        var (data, kind) = value.Type switch
        {
            RegValueType.String => ((object)(value.Text ?? ""), RegistryValueKind.String),
            RegValueType.ExpandString => (value.Text ?? "", RegistryValueKind.ExpandString),
            RegValueType.DWord => (unchecked((int)(uint)(value.Number ?? 0)), RegistryValueKind.DWord),
            RegValueType.QWord => (value.Number ?? 0L, RegistryValueKind.QWord),
            RegValueType.MultiString => (value.Multi ?? [], RegistryValueKind.MultiString),
            RegValueType.None => (value.Binary ?? [], RegistryValueKind.None),
            _ => (value.Binary ?? [], RegistryValueKind.Binary),
        };
        key.SetValue(value.Name, data, kind);
    }

    public void DeleteValue(RegHive hive, RegView view, string path, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(Map(hive), Map(view));
        using var key = baseKey.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKeyTree(RegHive hive, RegView view, string path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(Map(hive), Map(view));
        baseKey.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    private static RegistryKey OpenWritable(RegHive hive, RegView view, string path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(Map(hive), Map(view));
        return baseKey.OpenSubKey(path, writable: true) ?? throw new IOException($@"Registry key not found: {hive}\{path}");
    }

    private static RegistryHive Map(RegHive hive) => hive switch
    {
        RegHive.LocalMachine => RegistryHive.LocalMachine,
        RegHive.CurrentUser => RegistryHive.CurrentUser,
        RegHive.Users => RegistryHive.Users,
        RegHive.ClassesRoot => RegistryHive.ClassesRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    private static RegistryView Map(RegView view) => view switch
    {
        RegView.Registry64 => RegistryView.Registry64,
        RegView.Registry32 => RegistryView.Registry32,
        _ => RegistryView.Default,
    };
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistryKey(RegistryKey key) : IRegistryKey
{
    public string Path => key.Name;

    public IReadOnlyList<string> GetSubKeyNames() => Safe(key.GetSubKeyNames) ?? [];

    public IReadOnlyList<string> GetValueNames() => Safe(key.GetValueNames) ?? [];

    public IRegistryKey? OpenSubKey(string name)
    {
        var sub = Safe(() => key.OpenSubKey(name, writable: false));
        return sub is null ? null : new WindowsRegistryKey(sub);
    }

    public RegValue? GetValue(string name)
    {
        return Safe(() =>
        {
            var kind = key.GetValueKind(name);
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return kind switch
            {
                RegistryValueKind.String => new RegValue { Name = name, Type = RegValueType.String, Text = raw as string },
                RegistryValueKind.ExpandString => new RegValue { Name = name, Type = RegValueType.ExpandString, Text = raw as string },
                RegistryValueKind.DWord => new RegValue { Name = name, Type = RegValueType.DWord, Number = raw is int i ? unchecked((uint)i) : null },
                RegistryValueKind.QWord => new RegValue { Name = name, Type = RegValueType.QWord, Number = raw as long? },
                RegistryValueKind.MultiString => new RegValue { Name = name, Type = RegValueType.MultiString, Multi = raw as string[] },
                RegistryValueKind.Binary => new RegValue { Name = name, Type = RegValueType.Binary, Binary = raw as byte[] },
                RegistryValueKind.None => new RegValue { Name = name, Type = RegValueType.None, Binary = raw as byte[] },
                _ => new RegValue { Name = name, Type = RegValueType.Unknown, Binary = raw as byte[] },
            };
        });
    }

    public void Dispose() => key.Dispose();

    private static T? Safe<T>(Func<T?> action) where T : class
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
