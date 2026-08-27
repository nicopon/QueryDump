using System;
using System.Collections.Generic;
using System.Linq;

namespace DtPipe.Adapters.Common;

/// <summary>
/// Closed extension -> DuckDB format map for object-storage locations.
///
/// Deliberately a lookup and not a sniffer: the engine must never guess a format from content or
/// from adapter identity. An unknown extension is an error listing what is supported, which is
/// actionable, rather than a silent fallback that fails much later inside DuckDB.
/// </summary>
public static class ObjectFormatMap
{
    public sealed record FormatSpec(string ReadFunction, string CopyFormat);

    private static readonly Dictionary<string, FormatSpec> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".parquet"] = new("read_parquet", "PARQUET"),
        [".csv"] = new("read_csv", "CSV"),
        [".tsv"] = new("read_csv", "CSV"),
        [".json"] = new("read_json", "JSON"),
        [".jsonl"] = new("read_json", "JSON"),
        [".ndjson"] = new("read_json", "JSON"),
    };

    public static bool IsSupported(string extension) =>
        !string.IsNullOrEmpty(extension) && ByExtension.ContainsKey(extension);

    public static FormatSpec Resolve(ObjectUri uri)
    {
        if (ByExtension.TryGetValue(uri.Extension, out var spec)) return spec;

        var known = string.Join(", ", ByExtension.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var seen = string.IsNullOrEmpty(uri.Extension) ? "none" : uri.Extension;
        throw new InvalidOperationException(
            $"Cannot determine the format of '{uri.DuckDbUri}' (extension: {seen}). " +
            $"Object-storage locations are resolved by extension; supported: {known}.");
    }
}
