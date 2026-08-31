using Apache.Arrow;
using Apache.Arrow.Arrays;
using DtPipe.Core.Infrastructure.Arrow;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using ParquetField = Parquet.Schema.Field;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DtPipe.Adapters.Parquet;

public static class ArrowToParquetConverter
{
    public static Task WriteColumnAsync(ParquetRowGroupWriter writer, IArrowArray arrowArray, ParquetField field, CancellationToken ct = default)
    {
        if (field is ListField listField)
        {
            if (arrowArray is not ListArray listArray)
                throw new NotSupportedException(
                    $"Column '{field.Name}' is a Parquet list but the pipeline produced " +
                    $"{arrowArray.GetType().Name}. The schema and the data must agree.");

            return WriteListColumnAsync(writer, listArray, listField, ct);
        }

        var dataField = (DataField)field;
        bool isGuidField = dataField.ClrNullableIfHasNullsType == typeof(Guid)
                        || dataField.ClrNullableIfHasNullsType == typeof(Guid?);
        return arrowArray switch
        {
            Int32Array a => writer.WriteAsync<int>(dataField, ExtractPrimitiveValues<int, Int32Array>(a).AsMemory(), cancellationToken: ct),
            Int64Array a => writer.WriteAsync<long>(dataField, ExtractPrimitiveValues<long, Int64Array>(a).AsMemory(), cancellationToken: ct),
            DoubleArray a => writer.WriteAsync<double>(dataField, ExtractPrimitiveValues<double, DoubleArray>(a).AsMemory(), cancellationToken: ct),
            FloatArray a => writer.WriteAsync<float>(dataField, ExtractPrimitiveValues<float, FloatArray>(a).AsMemory(), cancellationToken: ct),
            BooleanArray a => writer.WriteAsync<bool>(dataField, ExtractBooleanValues(a).AsMemory(), cancellationToken: ct),
            StringArray a => writer.WriteAsync(dataField, (IReadOnlyCollection<string?>)ExtractStringValues(a)),
            Decimal128Array a => writer.WriteAsync<decimal>(dataField, ExtractDecimalValues<Decimal128Array>(a).AsMemory(), cancellationToken: ct),
            Decimal256Array a => writer.WriteAsync<decimal>(dataField, ExtractDecimalValues<Decimal256Array>(a).AsMemory(), cancellationToken: ct),
            Date64Array a => writer.WriteAsync<DateTime>(dataField, ExtractDate64Values(a).AsMemory(), cancellationToken: ct),
            // Timestamp: no-timezone fields are typed as DateTime — coerce DateTimeOffset to DateTime
            TimestampArray a when (Nullable.GetUnderlyingType(dataField.ClrNullableIfHasNullsType) ?? dataField.ClrNullableIfHasNullsType) == typeof(DateTime) =>
                writer.WriteAsync<DateTime>(dataField, ExtractTimestampAsDateTimeValues(a).AsMemory(), cancellationToken: ct),
            TimestampArray a => writer.WriteAsync<DateTimeOffset>(dataField, ExtractTimestampValues(a).AsMemory(), cancellationToken: ct),
            // FixedSizeBinaryArray(16) with arrow.uuid → DtPipe internal UUID format
            FixedSizeBinaryArray a when isGuidField => writer.WriteAsync<Guid>(dataField, ExtractGuidValuesFromFixed(a).AsMemory(), cancellationToken: ct),
            // BinaryArray legacy: kept for sources that still emit BinaryType for UUID
            BinaryArray a when isGuidField => writer.WriteAsync<Guid>(dataField, ExtractGuidValues(a).AsMemory(), cancellationToken: ct),
            BinaryArray a => writer.WriteAsync(dataField, (IReadOnlyCollection<byte[]?>)ExtractBinaryValues(a)),
            // FixedSizeBinaryArray without Guid field → generic binary bytes
            FixedSizeBinaryArray a => writer.WriteAsync(dataField, (IReadOnlyCollection<byte[]?>)ExtractFixedBinaryValues(a)),
            _ => throw new NotSupportedException($"Arrow array type {arrowArray.GetType().Name} is not supported for Parquet conversion yet.")
        };
    }


    /// <summary>
    /// Writes an Arrow list column as a Parquet LIST, in the three-level encoding the format
    /// defines. Levels are emitted per row, values only for present elements:
    /// <list type="bullet">
    /// <item>definition 0 — the list itself is NULL</item>
    /// <item>definition 1 — the list is present but empty</item>
    /// <item>definition <c>MaxDefinitionLevel - 1</c> — the element is NULL</item>
    /// <item>definition <c>MaxDefinitionLevel</c> — the element holds a value</item>
    /// <item>repetition 0 opens a row, 1 continues the current list</item>
    /// </list>
    /// The encoding was read back from a file DuckDB wrote, not inferred: a wrong level silently
    /// reshapes the data instead of failing.
    /// </summary>
    private static Task WriteListColumnAsync(ParquetRowGroupWriter writer, ListArray listArray, ListField listField, CancellationToken ct)
    {
        var itemField = (DataField)listField.Item;
        int maxDef = itemField.MaxDefinitionLevel;

        var definitions = new List<int>(listArray.Length);
        var repetitions = new List<int>(listArray.Length);
        var valueIndices = new List<int>(listArray.Values.Length);

        for (int row = 0; row < listArray.Length; row++)
        {
            if (listArray.IsNull(row))
            {
                definitions.Add(0);
                repetitions.Add(0);
                continue;
            }

            int start = listArray.ValueOffsets[row];
            int end = listArray.ValueOffsets[row + 1];

            if (start == end)
            {
                definitions.Add(1);
                repetitions.Add(0);
                continue;
            }

            for (int i = start; i < end; i++)
            {
                definitions.Add(listArray.Values.IsNull(i) ? maxDef - 1 : maxDef);
                repetitions.Add(i == start ? 0 : 1);
                if (!listArray.Values.IsNull(i)) valueIndices.Add(i);
            }
        }

        return WriteListValuesAsync(writer, itemField, listArray.Values, valueIndices,
            definitions.ToArray(), repetitions.ToArray(), ct);
    }

    private static Task WriteListValuesAsync(
        ParquetRowGroupWriter writer, DataField itemField, IArrowArray values,
        List<int> indices, int[] definitions, int[] repetitions, CancellationToken ct)
    {
        var defs = (ReadOnlyMemory<int>?)definitions.AsMemory();
        var reps = (ReadOnlyMemory<int>?)repetitions.AsMemory();

        return values switch
        {
            Int32Array a => writer.WriteAllPartsAsync<int>(itemField, Gather<int>(indices, a.GetValue).AsMemory(), defs, reps, ct),
            Int64Array a => writer.WriteAllPartsAsync<long>(itemField, Gather<long>(indices, a.GetValue).AsMemory(), defs, reps, ct),
            Int16Array a => writer.WriteAllPartsAsync<short>(itemField, Gather<short>(indices, a.GetValue).AsMemory(), defs, reps, ct),
            DoubleArray a => writer.WriteAllPartsAsync<double>(itemField, Gather<double>(indices, a.GetValue).AsMemory(), defs, reps, ct),
            FloatArray a => writer.WriteAllPartsAsync<float>(itemField, Gather<float>(indices, a.GetValue).AsMemory(), defs, reps, ct),
            BooleanArray a => writer.WriteAllPartsAsync<bool>(itemField, Gather<bool>(indices, a.GetValue).AsMemory(), defs, reps, ct),
            Decimal128Array a => writer.WriteAllPartsAsync<decimal>(itemField, Gather<decimal>(indices, a.GetValue).AsMemory(), defs, reps, ct),
            // Parquet.Net exposes definition levels only through WriteAllPartsAsync, whose T is
            // constrained to value types. The string overload carries repetition levels alone,
            // which cannot tell a NULL list from an empty one — so a text list is refused rather
            // than written with a null/empty distinction we could not honour.
            StringArray => throw new NotSupportedException(
                $"Parquet column '{itemField.Path}': lists of text are not supported yet. " +
                "Project the column in your query, for example with array_to_json(col)::text."),

            _ => throw new NotSupportedException(
                $"Parquet list of {values.GetType().Name} is not supported. " +
                "Cast the column in your query, for example to a list of a numeric type.")
        };
    }

    /// <summary>Present values only, in list order — nulls are carried by the definition levels.</summary>
    private static T[] Gather<T>(List<int> indices, Func<int, T?> get) where T : struct
    {
        var result = new T[indices.Count];
        for (int i = 0; i < indices.Count; i++) result[i] = get(indices[i])!.Value;
        return result;
    }

    private static Guid?[] ExtractGuidValuesFromFixed(FixedSizeBinaryArray array)
    {
        var result = new Guid?[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            if (array.IsNull(i)) { result[i] = null; continue; }
            result[i] = ArrowTypeMapper.FromArrowUuidBytes(array.GetBytes(i));
        }
        return result;
    }

    private static Guid?[] ExtractGuidValues(BinaryArray array)
    {
        var result = new Guid?[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            if (array.IsNull(i)) { result[i] = null; continue; }
            var bytes = array.GetBytes(i).ToArray();
            result[i] = bytes.Length == 16 ? new Guid(bytes) : null;
        }
        return result;
    }

    private static T?[] ExtractPrimitiveValues<T, TArray>(TArray array)
        where T : struct, IEquatable<T>
        where TArray : PrimitiveArray<T>
    {
        var result = new T?[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            result[i] = array.IsNull(i) ? null : array.GetValue(i);
        }
        return result;
    }

    private static decimal?[] ExtractDecimalValues<TArray>(TArray array)
        where TArray : IArrowArray
    {
        var result = new decimal?[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            result[i] = (decimal?)ArrowTypeMapper.GetValue(array, i);
        }
        return result;
    }

    private static bool?[] ExtractBooleanValues(BooleanArray array)
    {
        var result = new bool?[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            result[i] = array.IsNull(i) ? null : array.GetValue(i);
        }
        return result;
    }

    private static string?[] ExtractStringValues(StringArray array)
    {
        var result = new string?[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            result[i] = array.IsNull(i) ? null : array.GetString(i);
        }
        return result;
    }

    private static DateTime?[] ExtractDate64Values(Date64Array array)
    {
        var result = new DateTime?[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            result[i] = array.IsNull(i) ? null : array.GetDateTime(i);
        }
        return result;
    }

    private static DateTime?[] ExtractTimestampAsDateTimeValues(TimestampArray array)
    {
        var result = new DateTime?[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            if (array.IsNull(i)) { result[i] = null; continue; }
            var dto = array.GetTimestamp(i);
            result[i] = dto?.DateTime;
        }
        return result;
    }

    private static DateTimeOffset?[] ExtractTimestampValues(TimestampArray array)
    {
        var result = new DateTimeOffset?[array.Length];
        for (int i = 0; i < array.Length; i++)
        {
            result[i] = array.IsNull(i) ? null : array.GetTimestamp(i);
        }
        return result;
    }

    private static byte[][] ExtractBinaryValues(BinaryArray array)
    {
        var result = new byte[array.Length][];
        for (int i = 0; i < array.Length; i++)
        {
            result[i] = array.IsNull(i) ? [] : array.GetBytes(i).ToArray();
        }
        return result;
    }

    private static byte[][] ExtractFixedBinaryValues(FixedSizeBinaryArray array)
    {
        var result = new byte[array.Length][];
        for (int i = 0; i < array.Length; i++)
        {
            result[i] = array.IsNull(i) ? [] : array.GetBytes(i).ToArray();
        }
        return result;
    }
}
