using System.Text;
using AppMover.Core.Msi;
using AppMover.Core.Pe;

namespace AppMover.Tests.Fakes;

/// <summary>
/// Builds minimal PE files: one section holding the import table, the CLR header for managed code and a version
/// resource (RT_VERSION → VS_FIXEDFILEINFO) when a version is given.
/// </summary>
public static class PeBuilder
{
    private const int SectionRva = 0x1000;
    private const int SectionOffset = 0x200;
    private const int SectionSize = 0x1000;

    public static byte[] Build(ushort machine, string[] imports, string? clrVersion = null, Version? version = null)
    {
        var pe32Plus = machine != PeInfo.MachineX86;
        var optionalSize = pe32Plus ? 240 : 224;
        var file = new byte[SectionOffset + SectionSize];
        using var writer = new BinaryWriter(new MemoryStream(file));

        writer.Write((ushort)0x5A4D);
        writer.Seek(0x3C, SeekOrigin.Begin);
        writer.Write(0x40);

        writer.Seek(0x40, SeekOrigin.Begin);
        writer.Write(0x00004550);
        writer.Write(machine);
        writer.Write((ushort)1);         // sections
        writer.Write(new byte[12]);
        writer.Write((ushort)optionalSize);
        writer.Write((ushort)0x22);

        var optional = 0x40 + 24;
        writer.Write((ushort)(pe32Plus ? 0x20B : 0x10B));
        writer.Seek(optional + (pe32Plus ? 108 : 92), SeekOrigin.Begin);
        writer.Write(16);                // NumberOfRvaAndSizes
        var directories = optional + (pe32Plus ? 112 : 96);
        writer.Seek(directories + 1 * 8, SeekOrigin.Begin);
        writer.Write(SectionRva);        // import table
        writer.Write((imports.Length + 1) * 20);
        if (version is not null)
        {
            writer.Seek(directories + 2 * 8, SeekOrigin.Begin);
            writer.Write(SectionRva + 0xA00);
            writer.Write(0x100);
        }
        if (clrVersion is not null)
        {
            writer.Seek(directories + 14 * 8, SeekOrigin.Begin);
            writer.Write(SectionRva + 0x800);
            writer.Write(72);
        }

        writer.Seek(optional + optionalSize, SeekOrigin.Begin);
        writer.Write(Encoding.ASCII.GetBytes(".rdata\0\0"));
        writer.Write(SectionSize);       // virtual size
        writer.Write(SectionRva);
        writer.Write(SectionSize);       // raw size
        writer.Write(SectionOffset);

        // Import descriptors, then the DLL names.
        var nameRva = SectionRva + 0x100;
        for (var i = 0; i < imports.Length; i++)
        {
            writer.Seek(SectionOffset + i * 20 + 12, SeekOrigin.Begin);
            writer.Write(nameRva);
            writer.Seek(SectionOffset + nameRva - SectionRva, SeekOrigin.Begin);
            writer.Write(Encoding.ASCII.GetBytes(imports[i] + "\0"));
            nameRva += imports[i].Length + 1;
        }

        if (clrVersion is not null)
        {
            writer.Seek(SectionOffset + 0x800 + 8, SeekOrigin.Begin);
            writer.Write(SectionRva + 0x900); // metadata
            writer.Seek(SectionOffset + 0x900, SeekOrigin.Begin);
            writer.Write(0x424A5342);
            writer.Write(new byte[8]);
            var runtime = Encoding.ASCII.GetBytes(clrVersion + "\0\0\0\0");
            writer.Write(runtime.Length);
            writer.Write(runtime);
        }
        if (version is not null)
            WriteVersionResource(writer, version);
        return file;
    }

    /// <summary>Resource tree at section offset 0xA00: type 16 → name 1 → language 0x409 → VS_VERSIONINFO.</summary>
    private static void WriteVersionResource(BinaryWriter writer, Version version)
    {
        const int root = SectionOffset + 0xA00;
        void Directory(int at, uint id, uint target)
        {
            writer.Seek(at + 14, SeekOrigin.Begin);
            writer.Write((ushort)1);        // one id entry
            writer.Write(id);
            writer.Write(target);
        }
        Directory(root, 16, 0x80000000 | 0x18);
        Directory(root + 0x18, 1, 0x80000000 | 0x30);
        Directory(root + 0x30, 0x409, 0x48);
        writer.Seek(root + 0x48, SeekOrigin.Begin);
        writer.Write(SectionRva + 0xA00 + 0x60); // data RVA
        writer.Write(0x60);                       // size
        // VS_VERSIONINFO: header + "VS_VERSION_INFO", then VS_FIXEDFILEINFO (aligned) at +0x28.
        writer.Seek(root + 0x60 + 0x28, SeekOrigin.Begin);
        writer.Write(0xFEEF04BD);
        writer.Write(0x00010000);
        writer.Write((uint)((version.Major << 16) | version.Minor));
        writer.Write((uint)((version.Build << 16) | Math.Max(0, version.Revision)));
    }
}

/// <summary>Writes Inno Setup uninstall logs (format of Setup.UninstallLog.pas).</summary>
public sealed class InnoLogBuilder
{
    private readonly List<(ushort Type, uint Extra, string[] Data)> _records = [];

    public InnoLogBuilder Add(ushort type, uint extra, params string[] data)
    {
        _records.Add((type, extra, data));
        return this;
    }

    public byte[] Build(string appName = "Acme Inno", bool is64Bit = true, int flags = 1)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.Unicode, leaveOpen: true))
        {
            foreach (var (type, extra, data) in _records)
            {
                var bytes = new MemoryStream();
                foreach (var text in data)
                {
                    var encoded = Encoding.Unicode.GetBytes(text);
                    bytes.WriteByte(0xFE);
                    bytes.Write(BitConverter.GetBytes(-encoded.Length));
                    bytes.Write(encoded);
                }
                bytes.WriteByte(0xFF);
                writer.Write(type);
                writer.Write(extra);
                writer.Write((uint)bytes.Length);
                writer.Write(bytes.ToArray());
            }
        }

        using var file = new MemoryStream();
        using var output = new BinaryWriter(file);
        var header = new byte[448];
        Encoding.ASCII.GetBytes(is64Bit ? "Inno Setup Uninstall Log (b) 64-bit" : "Inno Setup Uninstall Log (b)").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("AcmeInno_is1").CopyTo(header, 64);
        Encoding.ASCII.GetBytes(appName).CopyTo(header, 192);
        output.Write(header);

        // Blocks of 4096 bytes at most: Size, ~Size, CRC (not checked by AppMover), payload.
        var stream = payload.ToArray();
        for (var offset = 0; offset < stream.Length; offset += 4096)
        {
            var size = Math.Min(4096, stream.Length - offset);
            output.Write((uint)size);
            output.Write(~(uint)size);
            output.Write(0);
            output.Write(stream, offset, size);
        }

        var bytesOut = file.ToArray();
        BitConverter.GetBytes(1048).CopyTo(bytesOut, 320);              // version
        BitConverter.GetBytes(_records.Count).CopyTo(bytesOut, 324);    // records
        BitConverter.GetBytes((uint)bytesOut.Length).CopyTo(bytesOut, 328); // end offset
        BitConverter.GetBytes(flags).CopyTo(bytesOut, 332);
        return bytesOut;
    }
}

/// <summary>Tables of a fake .msi database.</summary>
public sealed class FakeMsiDatabase : IMsiDatabaseReader
{
    public Dictionary<string, List<string?[]>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeMsiDatabase Table(string name, params string?[][] rows)
    {
        Tables[name] = rows.ToList();
        return this;
    }

    public IReadOnlyList<string?[]>? Query(string databasePath, string sql)
    {
        var table = sql[(sql.IndexOf("FROM `", StringComparison.Ordinal) + 6)..].TrimEnd('`');
        return Tables.GetValueOrDefault(table);
    }
}
