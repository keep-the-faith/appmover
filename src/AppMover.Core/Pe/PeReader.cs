using System.Text;

namespace AppMover.Core.Pe;

/// <summary>What AppMover needs to know about an executable or a DLL.</summary>
/// <param name="Machine">IMAGE_FILE_MACHINE_* (0x8664 x64, 0x14C x86, 0xAA64 ARM64).</param>
/// <param name="ClrVersion">Runtime version of a .NET assembly ("v4.0.30319"), null for native code.</param>
/// <param name="Imports">Imported and delay-loaded DLLs, lower case.</param>
public sealed record PeInfo(ushort Machine, string? ClrVersion, IReadOnlyList<string> Imports)
{
    public const ushort MachineX86 = 0x14C;
    public const ushort MachineX64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;

    public bool IsManaged => ClrVersion is not null;

    /// <summary>File version of the version resource (VS_FIXEDFILEINFO), null when the file has none.</summary>
    public Version? FileVersion { get; init; }

    /// <summary>"x64", "x86", "arm64", or null for other machines.</summary>
    public string? Architecture => Machine switch
    {
        MachineX64 => "x64",
        MachineX86 => "x86",
        MachineArm64 => "arm64",
        _ => null,
    };
}

/// <summary>Minimal Portable Executable reader: machine, imports, delay imports, CLR header.</summary>
public static class PeReader
{
    private const int ImportDirectory = 1;
    private const int ResourceDirectory = 2;
    private const int VersionResourceType = 16; // RT_VERSION
    private const uint FixedFileInfoSignature = 0xFEEF04BD;
    private const int DelayImportDirectory = 13;
    private const int ClrDirectory = 14;
    private const int MaxImports = 2048;

    private sealed record Section(uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawPointer);

    /// <summary>Reads a PE file from a seekable stream; returns null when it is not a valid PE file.</summary>
    public static PeInfo? Read(Stream stream)
    {
        try
        {
            return ReadCore(new BinaryReader(stream, Encoding.ASCII, leaveOpen: true));
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static PeInfo? ReadCore(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) // "MZ"
            return null;
        stream.Position = 0x3C;
        var peOffset = reader.ReadUInt32();
        if (peOffset > stream.Length - 24)
            return null;
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
            return null;

        var machine = reader.ReadUInt16();
        var sectionCount = reader.ReadUInt16();
        stream.Position += 12;
        var optionalHeaderSize = reader.ReadUInt16();
        stream.Position += 2;

        var optionalHeader = stream.Position;
        var magic = reader.ReadUInt16();
        var pe32Plus = magic == 0x20B;
        if (magic != 0x10B && !pe32Plus)
            return null;
        stream.Position = optionalHeader + (pe32Plus ? 108 : 92);
        var directoryCount = reader.ReadUInt32();
        var directories = new (uint Rva, uint Size)[Math.Min(directoryCount, 16)];
        for (var i = 0; i < directories.Length; i++)
            directories[i] = (reader.ReadUInt32(), reader.ReadUInt32());

        stream.Position = optionalHeader + optionalHeaderSize;
        var sections = new List<Section>(sectionCount);
        for (var i = 0; i < sectionCount; i++)
        {
            stream.Position += 8; // name
            var virtualSize = reader.ReadUInt32();
            var virtualAddress = reader.ReadUInt32();
            var rawSize = reader.ReadUInt32();
            var rawPointer = reader.ReadUInt32();
            stream.Position += 16;
            sections.Add(new Section(virtualAddress, virtualSize, rawSize, rawPointer));
        }

        long? Offset(uint rva)
        {
            var section = sections.FirstOrDefault(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
            return section is null ? null : rva - section.VirtualAddress + section.RawPointer;
        }

        string? ReadString(uint rva)
        {
            if (Offset(rva) is not { } offset || offset >= stream.Length)
                return null;
            stream.Position = offset;
            var bytes = new List<byte>();
            for (int b; bytes.Count < 256 && (b = stream.ReadByte()) > 0;)
                bytes.Add((byte)b);
            return Encoding.ASCII.GetString(bytes.ToArray()).ToLowerInvariant();
        }

        var imports = new List<string>();
        void ReadDescriptors(int directory, int descriptorSize, int nameOffset)
        {
            if (directory >= directories.Length || directories[directory].Rva == 0 || Offset(directories[directory].Rva) is not { } start)
                return;
            for (var i = 0; i < MaxImports; i++)
            {
                stream.Position = start + i * descriptorSize + nameOffset;
                var nameRva = reader.ReadUInt32();
                if (nameRva == 0)
                    break;
                var position = stream.Position;
                if (ReadString(nameRva) is { Length: > 0 } name)
                    imports.Add(name);
                stream.Position = position;
            }
        }

        // IMAGE_IMPORT_DESCRIPTOR (20 bytes, Name at +12), IMAGE_DELAYLOAD_DESCRIPTOR (32 bytes, DllNameRVA at +4).
        ReadDescriptors(ImportDirectory, 20, 12);
        ReadDescriptors(DelayImportDirectory, 32, 4);

        return new PeInfo(machine, ReadClrVersion(reader, directories, Offset), imports.Distinct().ToList())
        {
            FileVersion = ReadFileVersion(reader, directories, Offset),
        };
    }

    /// <summary>Resource tree RT_VERSION → first name → first language → data, then VS_FIXEDFILEINFO.dwFileVersionMS/LS.</summary>
    private static Version? ReadFileVersion(BinaryReader reader, (uint Rva, uint Size)[] directories, Func<uint, long?> offset)
    {
        if (directories.Length <= ResourceDirectory || directories[ResourceDirectory].Rva == 0
            || offset(directories[ResourceDirectory].Rva) is not { } root)
            return null;
        var stream = reader.BaseStream;

        // First entry of a resource directory (optionally the one with this id); returns its offset field.
        uint? Entry(long directory, int? id)
        {
            stream.Position = directory + 12;
            var count = reader.ReadUInt16() + reader.ReadUInt16(); // named + id entries
            for (var i = 0; i < Math.Min(count, 4096); i++)
            {
                var name = reader.ReadUInt32();
                var target = reader.ReadUInt32();
                if (id is null || name == id)
                    return target;
            }
            return null;
        }

        const uint subdirectory = 0x80000000;
        if (Entry(root, VersionResourceType) is not { } types || (types & subdirectory) == 0
            || Entry(root + (types & ~subdirectory), null) is not { } names || (names & subdirectory) == 0
            || Entry(root + (names & ~subdirectory), null) is not { } languages || (languages & subdirectory) != 0)
            return null;

        stream.Position = root + languages;
        var dataRva = reader.ReadUInt32();
        var size = reader.ReadUInt32();
        if (offset(dataRva) is not { } data)
            return null;
        stream.Position = data;
        var bytes = reader.ReadBytes((int)Math.Min(size, 1024));
        for (var i = 0; i + 16 <= bytes.Length; i += 4)
        {
            if (BitConverter.ToUInt32(bytes, i) != FixedFileInfoSignature)
                continue;
            var high = BitConverter.ToUInt32(bytes, i + 8);
            var low = BitConverter.ToUInt32(bytes, i + 12);
            return new Version((int)(high >> 16), (int)(high & 0xFFFF), (int)(low >> 16), (int)(low & 0xFFFF));
        }
        return null;
    }

    /// <summary>CLR header → metadata root → version string ("v4.0.30319").</summary>
    private static string? ReadClrVersion(BinaryReader reader, (uint Rva, uint Size)[] directories, Func<uint, long?> offset)
    {
        if (directories.Length <= ClrDirectory || directories[ClrDirectory].Rva == 0 || offset(directories[ClrDirectory].Rva) is not { } clr)
            return null;
        var stream = reader.BaseStream;
        stream.Position = clr + 8; // cb, MajorRuntimeVersion, MinorRuntimeVersion
        var metadataRva = reader.ReadUInt32();
        if (offset(metadataRva) is not { } metadata)
            return "";
        stream.Position = metadata;
        if (reader.ReadUInt32() != 0x424A5342) // "BSJB"
            return "";
        stream.Position += 8; // major, minor, reserved
        var length = reader.ReadUInt32();
        var version = reader.ReadBytes((int)Math.Min(length, 255));
        var end = Array.IndexOf(version, (byte)0);
        return Encoding.ASCII.GetString(version, 0, end < 0 ? version.Length : end);
    }
}
