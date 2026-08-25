using System.Globalization;
using Apache.Arrow;
using DtPipe.Core.Infrastructure.Arrow;
using Apache.Arrow.Types;
using DtPipe.Core.Models;

namespace DtPipe.Adapters.Xml;

/// <summary>
/// F15 — static type-inference helpers extracted from XmlStreamReader.
/// </summary>
internal static class XmlTypeInferrer
{
	internal static Type GetSimplifiedClrType(Field f)
	{
		return f.DataType switch
		{
			ListType or LargeListType or StructType or MapType => typeof(object),
			_ => ArrowTypeMapper.GetClrTypeFromField(f)
		};
	}
	internal static IArrowType ResolveHintToArrowType(string hint) => hint.ToLowerInvariant() switch
	{
		"uuid" or "guid" => ArrowTypeMapper.GetLogicalType(typeof(Guid)).ArrowType,
		"int32" => Int32Type.Default,
		"int64" => Int64Type.Default,
		"double" => DoubleType.Default,
		"float" => FloatType.Default,
		"decimal" => new Decimal128Type(38, 18),
		"bool" or "boolean" => BooleanType.Default,
		"datetime" => TimestampType.Default,
		"datetimeoffset" => TimestampType.Default,
		_ => StringType.Default
	};
	internal static Dictionary<string, Type> ParseColumnTypesOption(string spec)
	{
		var result = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(spec)) return result;

		foreach (var entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var idx = entry.IndexOf(':');
			if (idx <= 0) continue;
			var name = entry[..idx].Trim();
			var typeName = entry[(idx + 1)..].Trim();
			var clrType = ResolveHintToClrType(typeName);
			if (clrType != null) result[name] = clrType;
		}
		return result;
	}
	internal static IArrowType ClrToArrowType(Type clrType)
	{
		if (clrType == typeof(int)) return Int32Type.Default;
		if (clrType == typeof(long)) return Int64Type.Default;
		if (clrType == typeof(double)) return DoubleType.Default;
		if (clrType == typeof(float)) return FloatType.Default;
		if (clrType == typeof(decimal)) return new Decimal128Type(38, 18);
		if (clrType == typeof(bool)) return BooleanType.Default;
		if (clrType == typeof(DateTime)) return TimestampType.Default;
		if (clrType == typeof(DateTimeOffset)) return TimestampType.Default;
		if (clrType == typeof(Guid)) return ArrowTypeMapper.GetLogicalType(typeof(Guid)).ArrowType;
		return StringType.Default;
	}
	internal static Type? ResolveHintToClrType(string hint) => hint.ToLowerInvariant() switch
	{
		"uuid" or "guid" => typeof(Guid),
		"string" or "str" => typeof(string),
		"int" or "int32" => typeof(int),
		"long" or "int64" => typeof(long),
		"double" or "float64" => typeof(double),
		"float" or "float32" or "single" => typeof(float),
		"decimal" or "numeric" or "money" => typeof(decimal),
		"bool" or "boolean" => typeof(bool),
		"datetime" or "date" => typeof(DateTime),
		"datetimeoffset" or "timestamp" => typeof(DateTimeOffset),
		_ => null
	};
	internal static Dictionary<string, Func<string, object?>> BuildColumnParsers(Dictionary<string, Type> overrides)
	{
		var result = new Dictionary<string, Func<string, object?>>(StringComparer.OrdinalIgnoreCase);
		foreach (var kvp in overrides)
		{
			result[kvp.Key] = BuildParser(kvp.Value);
		}
		return result;
	}
	internal static Func<string, object?> BuildParser(Type clrType)
	{
		if (clrType == typeof(Guid))
			return static s => Guid.TryParse(s, out var g) ? g : (object?)null;

		if (clrType == typeof(int))
			return static s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (object?)null;

		if (clrType == typeof(long))
			return static s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (object?)null;

		if (clrType == typeof(double))
			return static s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (object?)null;

		if (clrType == typeof(float))
			return static s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (object?)null;

		if (clrType == typeof(decimal))
			return static s => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : (object?)null;

		if (clrType == typeof(bool))
			return static s =>
			{
				if (bool.TryParse(s, out var b)) return b;
				return s.ToLowerInvariant() switch { "1" or "yes" or "true" => true, "0" or "no" or "false" => false, _ => (object?)null };
			};

		if (clrType == typeof(DateTime))
			return static s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v) ? v : (object?)null;

		if (clrType == typeof(DateTimeOffset))
			return static s => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v) ? v : (object?)null;

		return static s => s;
	}
	internal static string? InferTypeHint(List<string> samples)
	{
		if (samples.Count == 0) return null;

		bool allMatch(Func<string, bool> test) => samples.All(test);

		if (allMatch(s => Guid.TryParse(s, out _))) return "uuid";

		if (allMatch(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
		{
			return samples.All(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) ? "int32" : "int64";
		}

		if (allMatch(s => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _) && s.Contains('.')))
		{
			bool needsDecimalPrecision = samples.Any(s =>
			{
				if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return false;
				if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl)) return true;
				return d != (decimal)dbl;
			});
			return needsDecimalPrecision ? "decimal" : "double";
		}

		if (allMatch(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return "double";

		if (allMatch(s => bool.TryParse(s, out _) || s.ToLowerInvariant() is "0" or "1" or "yes" or "no" or "true" or "false")) return "bool";

		if (allMatch(s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) return "datetime";

		return null;
	}
}
