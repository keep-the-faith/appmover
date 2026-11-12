using System.Buffers.Binary;
using System.Text;

namespace AppMover.Core.Inno;

/// <summary>One action recorded by Inno Setup to be undone at uninstall.</summary>
public sealed record InnoLogRecord(ushort Type, uint ExtraData, IReadOnlyList<string> Data);

/// <summary>Content of an Inno Setup uninstall log (unins000.dat).</summary>
public sealed record InnoLog(string AppId, string AppName, bool Is64BitMode, int Flags, IReadOnlyList<InnoLogRecord> Records)
{
    /// <summary>ufAdminInstalled or ufAdminInstallMode: HKEY_CLASSES_ROOT meant HKLM\SOFTWARE\Classes.</summary>
    public bool IsAdminInstall => (Flags & (1 << 0 | 1 << 7)) != 0;
}

/// <summary>
/// Reader of the Inno Setup uninstall log (format of Inno Setup's Setup.UninstallLog.pas):
/// a 448-byte header, then blocks (Size, ~Size, CRC32, payload ≤ 4096 bytes) forming a stream of records
/// (UInt16 type, UInt32 extra data, UInt32 data size, data = strings ended by 0xFF).
/// </summary>
public static class InnoUninstallLog
{
    public const ushort DeleteDirOrFiles = 0x81;
    public const ushort DeleteFile = 0x82;
    public const ushort DeleteGroupOrItem = 0x83;
    public const ushort RegDeleteEntireKey = 0x86;
    public const ushort RegClearValue = 0x87;
    public const ushort RegDeleteKeyIfEmpty = 0x88;
    public const ushort RegDeleteValue = 0x89;

    public const uint DeleteFileExistedBeforeInstall = 0x1;
    public const uint DeleteFileIsFont = 0x4;
    public const uint DeleteFileSharedFile = 0x8;
    public const uint DeleteDirIsDir = 0x2;
    public const uint DeleteDirExtra = 0x1;

    /// <summary>Registry records: root HKEY in the low bits, bit 24 for the 64-bit view.</summary>
    public const uint RegKeyHandleMask = 0x80FFFFFF;
    public const uint Reg64BitKey = 0x01000000;

    public const int HeaderSize = 448;
    private const string IdPrefix = "Inno Setup Uninstall Log";
    private const int MaxBlockSize = 4096;
    private const uint MaxDataSize = 0x08000000;

    /// <summary>Parses a log; returns null when the data is not a valid Inno Setup uninstall log.</summary>
    public static InnoLog? Read(Stream stream)
    {
        try
        {
            return ReadCore(stream);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    private static InnoLog? ReadCore(Stream stream)
    {
        var header = new byte[HeaderSize];
        stream.ReadExactly(header);
        var id = ReadAnsi(header.AsSpan(0, 64));
        if (!id.StartsWith(IdPrefix, StringComparison.Ordinal))
            return null;

        var recordCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(324));
        var endOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(328));
        var flags = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(332));

        // Reassemble the payload of the blocks.
        using var payload = new MemoryStream();
        var position = (long)HeaderSize;
        var blockHeader = new byte[12];
        while (position + blockHeader.Length <= endOffset)
        {
            stream.ReadExactly(blockHeader);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
            var notSize = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader.AsSpan(4));
            if (size != ~notSize || size > MaxBlockSize || position + blockHeader.Length + size > endOffset)
                throw new InvalidDataException("Corrupted block");
            var block = new byte[size];
            stream.ReadExactly(block);
            payload.Write(block);
            position += blockHeader.Length + size;
        }

        var data = payload.ToArray();
        var records = new List<InnoLogRecord>(Math.Max(0, recordCount));
        var offset = 0;
        for (var i = 0; i < recordCount; i++)
        {
            var span = data.AsSpan(offset);
            var type = BinaryPrimitives.ReadUInt16LittleEndian(span);
            var extra = BinaryPrimitives.ReadUInt32LittleEndian(span[2..]);
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(span[6..]);
            if (dataSize > MaxDataSize)
                throw new InvalidDataException("Record too large");
            records.Add(new InnoLogRecord(type, extra, ReadStrings(span.Slice(10, (int)dataSize))));
            offset += 10 + (int)dataSize;
        }

        return new InnoLog(ReadHeaderString(header.AsSpan(64, 128)), ReadHeaderString(header.AsSpan(192, 128)),
            id.Contains("64-bit", StringComparison.Ordinal), flags, records);
    }

    /// <summary>
    /// Strings of a record: prefix 0x00-0xFC = ANSI length, 0xFD = UInt16 ANSI length, 0xFE = Int32 length
    /// (negative: UTF-16 byte count), 0xFF = end.
    /// </summary>
    private static List<string> ReadStrings(ReadOnlySpan<byte> data)
    {
        var strings = new List<string>();
        var pos = 0;
        while (pos < data.Length && data[pos] != 0xFF)
        {
            int length;
            switch (data[pos])
            {
                case 0xFD:
                    length = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 1)..]);
                    pos += 3;
                    break;
                case 0xFE:
                    length = BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 1)..]);
                    pos += 5;
                    break;
                default:
                    length = data[pos];
                    pos += 1;
                    break;
            }
            if (length < 0)
            {
                strings.Add(Encoding.Unicode.GetString(data.Slice(pos, -length)));
                pos -= length;
            }
            else
            {
                strings.Add(Encoding.Latin1.GetString(data.Slice(pos, length)));
                pos += length;
            }
        }
        return strings;
    }

    /// <summary>Header strings: ANSI, or 0x01 followed by UTF-8; null-terminated.</summary>
    private static string ReadHeaderString(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        var bytes = end < 0 ? field : field[..end];
        return bytes.Length > 0 && bytes[0] == 1 ? Encoding.UTF8.GetString(bytes[1..]) : Encoding.Latin1.GetString(bytes);
    }

    private static string ReadAnsi(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }
}
