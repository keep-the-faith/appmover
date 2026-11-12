using System.Management;
using AppMover.Core.Platform;

namespace AppMover.Host;

/// <summary>Volume Shadow Copies through WMI (Win32_ShadowCopy), available on Windows client and server editions.</summary>
public sealed class WmiSnapshotProvider : ISnapshotProvider
{
    public IVolumeSnapshot Create(string volumeRoot)
    {
        using var shadowClass = new ManagementClass(new ManagementPath(@"\\.\root\cimv2:Win32_ShadowCopy"));
        using var input = shadowClass.GetMethodParameters("Create");
        input["Volume"] = volumeRoot;
        input["Context"] = "ClientAccessible";
        using var output = shadowClass.InvokeMethod("Create", input, null);

        var result = Convert.ToUInt32(output["ReturnValue"]);
        if (result != 0)
            throw new IOException($"Volume Shadow Copy of {volumeRoot} failed (Win32_ShadowCopy.Create returned {result}).");
        var id = (string)output["ShadowID"];

        using var searcher = new ManagementObjectSearcher(@"\\.\root\cimv2", $"SELECT * FROM Win32_ShadowCopy WHERE ID = '{id}'");
        var shadow = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
                     ?? throw new IOException($"Volume Shadow Copy {id} not found after its creation.");
        return new WmiSnapshot(shadow, (string)shadow["DeviceObject"]);
    }

    /// <param name="shadow">The Win32_ShadowCopy instance, deleted on dispose.</param>
    /// <param name="device">"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN".</param>
    private sealed class WmiSnapshot(ManagementObject shadow, string device) : IVolumeSnapshot
    {
        public string MapPath(string path) => $@"{device}\{path[3..]}";

        public void Dispose()
        {
            shadow.Delete();
            shadow.Dispose();
        }
    }
}
