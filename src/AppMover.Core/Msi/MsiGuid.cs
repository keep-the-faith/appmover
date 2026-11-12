using System.Text;

namespace AppMover.Core.Msi;

/// <summary>
/// Conversion between a GUID and its "packed" (a.k.a. squished/compressed) form used by Windows Installer
/// in registry keys such as Installer\UserData\&lt;SID&gt;\Products\&lt;packed&gt;.
/// </summary>
public static class MsiGuid
{
    /// <summary>{12345678-ABCD-EF01-2345-6789ABCDEF01} → 87654321DCBA10FE32547698BADCFE10.</summary>
    public static string? Pack(string guid)
    {
        if (!Guid.TryParse(guid, out var parsed))
            return null;
        return Transform(parsed.ToString("N").ToUpperInvariant());
    }

    /// <summary>True when the text is a braced GUID, like an MSI product code.</summary>
    public static bool IsBracedGuid(string? text) => text is not null && Guid.TryParseExact(text, "B", out _);

    private static string Transform(string hex)
    {
        var sb = new StringBuilder(32);
        sb.Append(Reverse(hex[..8])).Append(Reverse(hex[8..12])).Append(Reverse(hex[12..16]));
        for (var i = 16; i < 32; i += 2)
            sb.Append(hex[i + 1]).Append(hex[i]);
        return sb.ToString();
    }

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}
