using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AppMover.Packaging;

/// <summary>Raised when a package is encrypted and no password was given.</summary>
public sealed class PackageEncryptedException(string path) : Exception($"{path} is encrypted: a password is required.");

/// <summary>
/// Password encryption of a whole package: AES-256-CBC, then HMAC-SHA256 over everything (encrypt-then-MAC).
/// Keys: PBKDF2-SHA256 of the password (64 bytes: encryption key + MAC key).
/// Layout: "AMENC001" | iterations (Int32 LE) | salt (16) | IV (16) | ciphertext | HMAC (32).
/// </summary>
public static class PackageCrypto
{
    public const int DefaultIterations = 600_000;
    private static readonly byte[] Magic = "AMENC001"u8.ToArray();
    private const int HeaderSize = 8 + 4 + 16 + 16;
    private const int MacSize = 32;

    /// <summary>True when the stream starts with the encryption header (its position is restored).</summary>
    public static bool IsEncrypted(Stream stream)
    {
        var position = stream.Position;
        var buffer = new byte[Magic.Length];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        stream.Position = position;
        return read == Magic.Length && buffer.AsSpan().SequenceEqual(Magic);
    }

    public static bool IsEncrypted(string path)
    {
        using var stream = File.OpenRead(path);
        return IsEncrypted(stream);
    }

    public static void Encrypt(Stream input, Stream output, string password, int iterations = DefaultIterations)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), iterations);
        RandomNumberGenerator.Fill(header.AsSpan(12, 16));   // salt
        RandomNumberGenerator.Fill(header.AsSpan(28, 16));   // IV
        var (encryptionKey, macKey) = DeriveKeys(password, header.AsSpan(12, 16).ToArray(), iterations);

        using var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        output.Write(header);
        mac.AppendData(header);

        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.IV = header.AsSpan(28, 16).ToArray();
        using (var encryptor = aes.CreateEncryptor())
        using (var crypto = new CryptoStream(new MacWriteStream(output, mac), encryptor, CryptoStreamMode.Write, leaveOpen: true))
            input.CopyTo(crypto);
        output.Write(mac.GetHashAndReset());
    }

    /// <summary>Checks the password and the integrity of the whole input, then decrypts it.</summary>
    /// <exception cref="CryptographicException">Wrong password or corrupted package.</exception>
    public static void Decrypt(Stream input, Stream output, string password)
    {
        var start = input.Position;
        var header = new byte[HeaderSize];
        input.ReadExactly(header);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Not an encrypted AppMover package.");
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var cipherLength = input.Length - start - HeaderSize - MacSize;
        if (iterations <= 0 || cipherLength <= 0)
            throw new CryptographicException("Corrupted package.");
        var (encryptionKey, macKey) = DeriveKeys(password, header.AsSpan(12, 16).ToArray(), iterations);

        // 1. Authenticate before decrypting anything.
        using (var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey))
        {
            mac.AppendData(header);
            CopyTo(input, cipherLength, mac.AppendData);
            var expected = new byte[MacSize];
            input.ReadExactly(expected);
            if (!CryptographicOperations.FixedTimeEquals(mac.GetHashAndReset(), expected))
                throw new CryptographicException("Wrong password, or the package is corrupted.");
        }

        // 2. Decrypt.
        input.Position = start + HeaderSize;
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.IV = header.AsSpan(28, 16).ToArray();
        using var decryptor = aes.CreateDecryptor();
        using var crypto = new CryptoStream(output, decryptor, CryptoStreamMode.Write, leaveOpen: true);
        CopyTo(input, cipherLength, crypto.Write);
        crypto.FlushFinalBlock();
    }

    private static (byte[] EncryptionKey, byte[] MacKey) DeriveKeys(string password, byte[] salt, int iterations)
    {
        var keys = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 64);
        return (keys[..32], keys[32..]);
    }

    private static void CopyTo(Stream input, long length, Action<byte[], int, int> write)
    {
        var buffer = new byte[81920];
        while (length > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read <= 0)
                throw new CryptographicException("Corrupted package (truncated).");
            write(buffer, 0, read);
            length -= read;
        }
    }

    /// <summary>Writes to the output and feeds the MAC with the same bytes.</summary>
    private sealed class MacWriteStream(Stream output, IncrementalHash mac) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            mac.AppendData(buffer, offset, count);
            output.Write(buffer, offset, count);
        }

        public override void Flush() => output.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
