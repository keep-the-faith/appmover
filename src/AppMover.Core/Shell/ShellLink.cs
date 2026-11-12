using System.Buffers.Binary;
using System.Text;

namespace AppMover.Core.Shell;

/// <summary>Data of a .lnk shortcut.</summary>
public sealed record ShellLinkInfo
{
    /// <summary>Target path (environment variables not expanded), or null for advertised / shell-item shortcuts.</summary>
    public string? Target { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? IconLocation { get; init; }
    public string? Description { get; init; }
    public string? RelativePath { get; init; }
    /// <summary>Set for Windows Installer "advertised" shortcuts, whose target is resolved by msi.dll at launch.</summary>
    public string? DarwinId { get; init; }
}

/// <summary>Minimal parser of the Shell Link binary format ([MS-SHLLINK]), independent of the Windows shell.</summary>
public static class ShellLink
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasRelativePath = 0x8;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;
    private const uint EnvironmentBlock = 0xA0000001;
    private const uint DarwinBlock = 0xA0000006;
    private const uint IconEnvironmentBlock = 0xA0000007;

    private static readonly Guid LinkClsid = new("00021401-0000-0000-C000-000000000046");

    /// <summary>Parses a shortcut; returns null when the data is not a valid shell link.</summary>
    public static ShellLinkInfo? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            return ParseCore(data);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ShellLinkInfo? ParseCore(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderSize
            || new Guid(data.Slice(4, 16)) != LinkClsid)
            return null;

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var unicode = (flags & IsUnicode) != 0;
        var pos = HeaderSize;

        if ((flags & HasLinkTargetIdList) != 0)
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);

        string? linkInfoPath = null;
        if ((flags & HasLinkInfo) != 0)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            linkInfoPath = ReadLinkInfoPath(data.Slice(pos, size));
            pos += size;
        }

        var description = ReadStringData(data, flags, HasName, unicode, ref pos);
        var relativePath = ReadStringData(data, flags, HasRelativePath, unicode, ref pos);
        var workingDir = ReadStringData(data, flags, HasWorkingDir, unicode, ref pos);
        var arguments = ReadStringData(data, flags, HasArguments, unicode, ref pos);
        var iconLocation = ReadStringData(data, flags, HasIconLocation, unicode, ref pos);

        string? environmentTarget = null;
        string? iconEnvironment = null;
        string? darwinId = null;
        while (pos + 8 <= data.Length)
        {
            var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            if (blockSize < 8 || pos + blockSize > data.Length)
                break;
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            var block = data.Slice(pos, blockSize);
            switch (signature)
            {
                case EnvironmentBlock:
                    environmentTarget = ReadEnvironmentBlock(block);
                    break;
                case IconEnvironmentBlock:
                    iconEnvironment = ReadEnvironmentBlock(block);
                    break;
                case DarwinBlock:
                    darwinId = ReadEnvironmentBlock(block);
                    break;
            }
            pos += blockSize;
        }

        return new ShellLinkInfo
        {
            Target = NullIfEmpty(environmentTarget) ?? NullIfEmpty(linkInfoPath),
            Arguments = NullIfEmpty(arguments),
            WorkingDirectory = NullIfEmpty(workingDir),
            IconLocation = NullIfEmpty(iconEnvironment) ?? NullIfEmpty(iconLocation),
            Description = NullIfEmpty(description),
            RelativePath = NullIfEmpty(relativePath),
            DarwinId = NullIfEmpty(darwinId),
        };
    }

    /// <summary>One StringData structure (character count + characters), present when its flag is set.</summary>
    private static string? ReadStringData(ReadOnlySpan<byte> data, uint flags, uint flag, bool unicode, ref int pos)
    {
        if ((flags & flag) == 0)
            return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        pos += 2;
        var length = unicode ? count * 2 : count;
        var text = (unicode ? Encoding.Unicode : Encoding.Latin1).GetString(data.Slice(pos, length));
        pos += length;
        return text;
    }

    /// <summary>LocalBasePath + CommonPathSuffix (Unicode versions when present).</summary>
    private static string? ReadLinkInfoPath(ReadOnlySpan<byte> info)
    {
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if ((infoFlags & 0x1) == 0) // VolumeIDAndLocalBasePath
            return null;

        if (headerSize >= 0x24)
        {
            var baseUnicode = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x1C..]);
            var suffixUnicode = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x20..]);
            return ReadNullTerminatedUnicode(info[baseUnicode..]) + ReadNullTerminatedUnicode(info[suffixUnicode..]);
        }

        var basePath = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x10..]);
        var suffix = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x18..]);
        return ReadNullTerminatedAnsi(info[basePath..]) + ReadNullTerminatedAnsi(info[suffix..]);
    }

    /// <summary>Environment / icon environment / darwin blocks: ANSI[260] then Unicode[260].</summary>
    private static string? ReadEnvironmentBlock(ReadOnlySpan<byte> block)
    {
        const int ansiSize = 260;
        if (block.Length >= 8 + ansiSize + ansiSize * 2)
        {
            var unicode = ReadNullTerminatedUnicode(block.Slice(8 + ansiSize, ansiSize * 2));
            if (unicode.Length > 0)
                return unicode;
        }
        return block.Length >= 8 + ansiSize ? ReadNullTerminatedAnsi(block.Slice(8, ansiSize)) : null;
    }

    private static string ReadNullTerminatedAnsi(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? data : data[..end]);
    }

    private static string ReadNullTerminatedUnicode(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
                return Encoding.Unicode.GetString(data[..i]);
        }
        return Encoding.Unicode.GetString(data[..(data.Length & ~1)]);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
