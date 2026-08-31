using System;
using System.Collections.Generic;
using System.Data.Common;
using Apache.Arrow.Types;

namespace Apache.Arrow.Ado;

/// <summary>
/// Configuration for the ADO.NET to Arrow conversion process.
/// </summary>
public sealed class AdoToArrowConfig
{
    public const int DefaultTargetBatchSize = 1024;

    /// <summary>
    /// Gets the target size (number of rows) for each RecordBatch.
    /// The actual batch size might be smaller for the last batch.
    /// </summary>
    public int TargetBatchSize { get; }

    /// <summary>
    /// Gets a soft upper bound, in bytes, on a buffered batch. A batch is flushed as soon as
    /// either <see cref="TargetBatchSize"/> rows or this many bytes accumulate. <c>0</c> disables
    /// the byte bound. Best-effort estimate — the row that crosses the bound is kept.
    /// </summary>
    public long MaxBatchBytes { get; }

    /// <summary>
    /// Gets whether to include DB column metadata in the Arrow schema.
    /// </summary>
    public bool IncludeMetadata { get; }

    /// <summary>
    /// Gets the function that maps a <see cref="DbColumn"/> to an Arrow type.
    /// Defaults to <see cref="AdoToArrowUtils.GetLogicalTypeFromDbColumn"/>.
    /// Inject a custom resolver to align the Arrow schema with your type system
    /// (e.g. to use ArrowTypeMapper from DtPipe.Core for pipeline consistency).
    /// </summary>
    public Func<DbColumn, Apache.Arrow.Serialization.Mapping.ArrowTypeResult> TypeResolver { get; }

    /// <summary>
    /// Gets the exact-match, case-insensitive overrides applied before <see cref="TypeResolver"/>.
    /// Keyed on <see cref="System.Data.Common.DbColumn.DataTypeName"/>. Built from
    /// <see cref="AdoToArrowConfigBuilder.AddDataTypeNameOverride"/>.
    /// </summary>
    public IReadOnlyDictionary<string, Apache.Arrow.Serialization.Mapping.ArrowTypeResult> DataTypeNameOverrides { get; }

    internal AdoToArrowConfig(
        int targetBatchSize,
        long maxBatchBytes,
        bool includeMetadata,
        Func<DbColumn, Apache.Arrow.Serialization.Mapping.ArrowTypeResult> typeResolver,
        IReadOnlyDictionary<string, Apache.Arrow.Serialization.Mapping.ArrowTypeResult> dataTypeNameOverrides)
    {
        TargetBatchSize = targetBatchSize;
        MaxBatchBytes = maxBatchBytes;
        IncludeMetadata = includeMetadata;
        TypeResolver = typeResolver;
        DataTypeNameOverrides = dataTypeNameOverrides;
    }
}
