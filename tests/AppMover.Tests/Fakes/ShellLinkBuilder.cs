using System.Text;

namespace AppMover.Tests.Fakes;

/// <summary>Builds minimal .lnk files ([MS-SHLLINK]) for tests.</summary>
public static class ShellLinkBuilder
{
    private const uint HasLinkInfo = 0x2;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;

    /// <summary>Shortcut with a LinkInfo local path (ANSI) and optional Unicode arguments.</summary>
    public static byte[] Target(string target, string? arguments = null)
    {
        var flags = HasLinkInfo | IsUnicode | (arguments is null ? 0 : HasArguments);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteHeader(writer, flags);

        // LinkInfo with a 0x1C-byte header, LocalBasePath then an empty CommonPathSuffix.
        var basePath = Encoding.Latin1.GetBytes(target + "\0");
        var size = 0x1C + basePath.Length + 1;
        writer.Write(size);
        writer.Write(0x1C);           // LinkInfoHeaderSize
        writer.Write(1);              // VolumeIDAndLocalBasePath
        writer.Write(0);              // VolumeIDOffset (not read)
        writer.Write(0x1C);           // LocalBasePathOffset
        writer.Write(0);              // CommonNetworkRelativeLinkOffset
        writer.Write(0x1C + basePath.Length); // CommonPathSuffixOffset
        writer.Write(basePath);
        writer.Write((byte)0);

        if (arguments is not null)
            WriteString(writer, arguments);
        writer.Write(0); // TerminalBlock
        return stream.ToArray();
    }

    /// <summary>Windows Installer advertised shortcut: no target, a Darwin block and an icon in C:\Windows\Installer.</summary>
    public static byte[] Advertised(string darwinId, string iconLocation)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteHeader(writer, HasIconLocation | IsUnicode);
        WriteString(writer, iconLocation);

        writer.Write(8 + 260 + 520);
        writer.Write(0xA0000006);
        var ansi = new byte[260];
        Encoding.Latin1.GetBytes(darwinId).CopyTo(ansi, 0);
        writer.Write(ansi);
        var unicode = new byte[520];
        Encoding.Unicode.GetBytes(darwinId).CopyTo(unicode, 0);
        writer.Write(unicode);
        writer.Write(0);
        return stream.ToArray();
    }

    private static void WriteHeader(BinaryWriter writer, uint flags)
    {
        writer.Write(0x4C);
        writer.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        writer.Write(flags);
        writer.Write(new byte[0x4C - 0x18]);
    }

    private static void WriteString(BinaryWriter writer, string text)
    {
        writer.Write((ushort)text.Length);
        writer.Write(Encoding.Unicode.GetBytes(text));
    }
}
