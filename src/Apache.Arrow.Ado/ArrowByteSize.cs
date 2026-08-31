using Apache.Arrow.Types;

namespace Apache.Arrow.Ado;

/// <summary>
/// Best-effort per-value byte-size estimates, used to bound a read batch by memory as well as by
/// row count. Deliberately approximate: fixed-width types report their storage width; the
/// variable-width types (<see cref="StringType"/>, <see cref="BinaryType"/>) report only the
/// offset-buffer slot, and the caller adds the payload length per value.
/// </summary>
public static class ArrowByteSize
{
    /// <summary>
    /// Storage width in bytes for a fixed-width Arrow type; for variable-width types, the size of
    /// one offset-buffer entry. The validity bitmap and per-batch overhead are not counted — the
    /// estimate is a floor, and the row that crosses a byte bound is kept, so a small undercount
    /// only means a slightly larger batch.
    /// </summary>
    public static int FixedWidth(IArrowType arrowType) => arrowType switch
    {
        BooleanType or Int8Type or UInt8Type => 1,
        Int16Type or UInt16Type => 2,
        Int32Type or UInt32Type or FloatType or Date32Type or Time32Type => 4,
        Int64Type or UInt64Type or DoubleType or Date64Type or Time64Type or TimestampType or DurationType => 8,
        Decimal128Type => 16,
        Decimal256Type => 32,
        FixedSizeBinaryType fsb => fsb.ByteWidth,
        StringType or BinaryType => 4,
        _ => 8,
    };
}
