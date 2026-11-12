using System.Text;

namespace AppMover.Core.Text;

/// <summary>A decoded text file and how to encode it back identically.</summary>
public sealed record DecodedText(string Text, Encoding Encoding, bool HasPreamble);

/// <summary>Decodes configuration files whatever their encoding, and re-encodes them the same way.</summary>
public static class TextCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// UTF-8/UTF-16 with BOM, UTF-8 without BOM, otherwise Latin-1 (byte-transparent, so ANSI files are rewritten
    /// without altering the characters that are not paths). Returns null for binary data.
    /// </summary>
    public static DecodedText? Decode(byte[] bytes)
    {
        DecodedText decoded;
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            decoded = new DecodedText(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true), true);
        else if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            decoded = new DecodedText(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode, true);
        else if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            decoded = new DecodedText(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode, true);
        else
        {
            try
            {
                decoded = new DecodedText(StrictUtf8.GetString(bytes), new UTF8Encoding(false), false);
            }
            catch (DecoderFallbackException)
            {
                decoded = new DecodedText(Encoding.Latin1.GetString(bytes), Encoding.Latin1, false);
            }
        }
        return decoded.Text.Contains('\0') ? null : decoded;
    }

    /// <summary>Encodes a new text like the original (same encoding, same BOM).</summary>
    public static byte[] Encode(DecodedText original, string text)
    {
        var body = original.Encoding.GetBytes(text);
        return original.HasPreamble ? [.. original.Encoding.GetPreamble(), .. body] : body;
    }
}
