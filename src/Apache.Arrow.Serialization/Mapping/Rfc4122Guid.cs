using System;

namespace Apache.Arrow.Serialization.Mapping;

/// <summary>
/// Byte order for a UUID as RFC 4122 defines it: all fields big-endian.
/// <para>
/// .NET stores the first three components of a <see cref="Guid"/> little-endian, so any format
/// specifying RFC 4122 layout needs the swap. Arrow's canonical UUID
/// (<c>FixedSizeBinary(16)</c> + <c>ARROW:extension:name = arrow.uuid</c>) is one such format —
/// <see cref="ArrowTypeMap.ToArrowUuidBytes"/> is its spelling of these methods — and a database
/// <c>BINARY(16)</c> column is another, on a path that never touches Arrow.
/// </para>
/// </summary>
public static class Rfc4122Guid
{
    /// <summary>Converts a .NET <see cref="Guid"/> to RFC 4122 big-endian bytes.</summary>
    public static byte[] ToBigEndianBytes(Guid guid)
    {
        var bytes = guid.ToByteArray();
        System.Array.Reverse(bytes, 0, 4); // component A: little → big
        System.Array.Reverse(bytes, 4, 2); // component B: little → big
        System.Array.Reverse(bytes, 6, 2); // component C: little → big
        // components D-E (bytes 8-15) are already big-endian in .NET
        return bytes;
    }

    /// <summary>Converts RFC 4122 big-endian bytes back to a .NET <see cref="Guid"/>.</summary>
    public static Guid FromBigEndianBytes(ReadOnlySpan<byte> b)
    {
        var copy = b.ToArray();
        System.Array.Reverse(copy, 0, 4);
        System.Array.Reverse(copy, 4, 2);
        System.Array.Reverse(copy, 6, 2);
        return new Guid(copy);
    }
}
