using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DtPipe.Sessions;

/// <summary>
/// Frames and encrypts a checkpoint file with <see cref="AesGcm"/> from the BCL — hardware
/// accelerated, and zero new dependencies.
///
/// <code>
///   header (32 B) : "DTPCKPT1" | version u16 | reserved u16 | nonce prefix (4 B) | reserved
///   frame (× n)   : ciphertext length u32 | counter u64 | tag (16 B) | ciphertext
/// </code>
///
/// <b>The nonce is a per-file random prefix followed by a strictly increasing counter.</b> Nonce
/// reuse is the one mistake that breaks GCM outright, so it is made structurally impossible
/// rather than left to a random draw: the counter cannot repeat within a file, and the random
/// prefix keeps two files from sharing a nonce space under the same key. Writing stops at
/// 2^32 frames.
///
/// <b>The header is authenticated as associated data.</b> An altered header fails the tag check
/// instead of silently changing how the body is read.
///
/// One frame holds one batch, so reading the first ten rows decrypts one frame — not the file.
/// </summary>
public static class CheckpointCipher
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DTPCKPT1");

    public const int HeaderSize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int NoncePrefixSize = 4;
    private const ushort FormatVersion = 1;
    private const long MaxFrames = uint.MaxValue;

    /// <summary>Builds a header with a fresh random nonce prefix.</summary>
    public static byte[] CreateHeader()
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), FormatVersion);
        RandomNumberGenerator.Fill(header.AsSpan(12, NoncePrefixSize));
        return header;
    }

    public static void ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize || !header[..8].SequenceEqual(Magic))
            throw new CryptographicException("Not a dtpipe checkpoint file.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2));
        if (version != FormatVersion)
            throw new CryptographicException($"Checkpoint format version {version} is not supported by this build.");
    }

    /// <summary>Writes one encrypted frame. <paramref name="counter"/> must never repeat for a file.</summary>
    public static void WriteFrame(Stream output, byte[] key, ReadOnlySpan<byte> header, long counter, ReadOnlySpan<byte> plaintext)
    {
        if (counter >= MaxFrames)
            throw new CryptographicException(
                "Checkpoint frame counter exhausted. Continuing would reuse a nonce, which breaks AES-GCM outright.");

        var nonce = BuildNonce(header, counter);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, header);

        Span<byte> prologue = stackalloc byte[4 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(prologue[..4], (uint)ciphertext.Length);
        BinaryPrimitives.WriteInt64LittleEndian(prologue.Slice(4, 8), counter);

        output.Write(prologue);
        output.Write(tag);
        output.Write(ciphertext);
    }

    /// <summary>Reads and authenticates the next frame, or null at end of file.</summary>
    public static byte[]? ReadFrame(Stream input, byte[] key, ReadOnlySpan<byte> header)
    {
        Span<byte> prologue = stackalloc byte[4 + 8];
        if (!TryReadExactly(input, prologue)) return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(prologue[..4]);
        var counter = BinaryPrimitives.ReadInt64LittleEndian(prologue.Slice(4, 8));

        var tag = new byte[TagSize];
        if (!TryReadExactly(input, tag)) throw new CryptographicException("Checkpoint frame is truncated.");

        var ciphertext = new byte[length];
        if (!TryReadExactly(input, ciphertext)) throw new CryptographicException("Checkpoint frame is truncated.");

        var plaintext = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        // Throws CryptographicException on any alteration to the ciphertext, the tag or the
        // header. Nothing partial is ever returned: a doubtful frame is no frame.
        aes.Decrypt(BuildNonce(header, counter), ciphertext, tag, plaintext, header);
        return plaintext;
    }

    private static byte[] BuildNonce(ReadOnlySpan<byte> header, long counter)
    {
        var nonce = new byte[NonceSize];
        header.Slice(12, NoncePrefixSize).CopyTo(nonce);
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(NoncePrefixSize), counter);
        return nonce;
    }

    private static bool TryReadExactly(Stream input, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = input.Read(buffer[read..]);
            if (n == 0) return read == 0 ? false : throw new CryptographicException("Checkpoint frame is truncated.");
            read += n;
        }
        return true;
    }
}
