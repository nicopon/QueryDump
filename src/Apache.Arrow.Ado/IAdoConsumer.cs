using System;
using System.Data.Common;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Apache.Arrow.Ado;

/// <summary>
/// Defines a consumer that extracts data from a specific column of a <see cref="DbDataReader"/>
/// and appends it to an internal <see cref="IArrowArrayBuilder"/>.
/// </summary>
public interface IAdoConsumer : IDisposable
{
    /// <summary>
    /// Gets the Arrow type associated with this consumer.
    /// </summary>
    IArrowType ArrowType { get; }

    /// <summary>
    /// Consumes the current row from the reader and appends the value to the builder.
    /// Handles nulls appropriately.
    /// </summary>
    /// <param name="reader">The reader to consume from.</param>
    void Consume(DbDataReader reader);

    /// <summary>
    /// Builds and returns the <see cref="IArrowArray"/> from the accumulated values.
    /// </summary>
    /// <returns>The constructed Arrow array.</returns>
    IArrowArray BuildArray();

    /// <summary>
    /// Resets the internal builder for a new batch.
    /// </summary>
    void Reset();

    /// <summary>
    /// Approximate size, in bytes, of the values appended since the last <see cref="Reset"/>.
    /// Best-effort: variable-width columns count their payload, fixed-width columns their type
    /// width. Used to bound a batch by memory as well as by row count.
    /// </summary>
    long EstimatedByteSize { get; }
}
