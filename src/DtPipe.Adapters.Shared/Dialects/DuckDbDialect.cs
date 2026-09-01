using DtPipe.Core.Abstractions;

namespace DtPipe.Core.Dialects;

public class DuckDbDialect : BaseSqlDialect
{
	/// <summary>DuckDB enforces read-only at connection open (access_mode=read_only), not through a statement, so there is nothing to emit here.</summary>
	public override string? ReadOnlySessionSql => null;

	private static readonly HashSet<string> ReservedKeywords = new(StringComparer.OrdinalIgnoreCase)
	{
		"ALL", "ANALYSE", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC", "AUTHORIZATION", "BINARY", "BOTH", "CASE", "CAST", "CHECK", "COLLATE", "COLLATION", "COLUMN", "CONCURRENTLY", "CONSTRAINT", "CREATE", "CROSS", "CURRENT_CATALOG", "CURRENT_DATE", "CURRENT_ROLE", "CURRENT_SCHEMA", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DEFAULT", "DEFERRABLE", "DESC", "DISTINCT", "DO", "ELSE", "END", "EXCEPT", "FALSE", "FETCH", "FOR", "FOREIGN", "FREEZE", "FROM", "FULL", "GRANT", "GROUP", "HAVING", "ILIKE", "IN", "INITIALLY", "INNER", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "LATERAL", "LEADING", "LEFT", "LIKE", "LIMIT", "LOCALTIMESTAMP", "NATURAL", "NOT", "NOTNULL", "NULL", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUTER", "OVERLAPS", "PLACING", "PRIMARY", "REFERENCES", "RETURNING", "RIGHT", "SELECT", "SESSION_USER", "SIMILAR", "SOME", "SYMMETRIC", "TABLE", "THEN", "TO", "TRAILING", "TRUE", "UNION", "UNIQUE", "USER", "USING", "VARIADIC", "VERBOSE", "WHEN", "WHERE", "WINDOW", "WITH"
	};

	public override string Normalize(string identifier)
	{
		// DuckDB is generally case-insensitive for unquoted SQL identifiers.
		// Treated like SQLite/SQLServer: Check for keywords/special chars.
		return identifier;
	}

	public override string Quote(string identifier)
	{
		return $"\"{identifier}\"";
	}

	protected override bool IsReservedKeyword(string identifier)
	{
		return ReservedKeywords.Contains(identifier);
	}

	protected override bool IsCaseMismatch(string identifier)
	{
		// DuckDB normalizes unquoted identifiers to lowercase (like PostgreSQL)
		// Quote if contains uppercase to preserve case
		return identifier != identifier.ToLowerInvariant();
	}

	public override string? TableDiscoveryQuery => "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY table_name";

	// F9 — DuckDB requires a PRIMARY KEY/UNIQUE constraint matching the keys for native
	// ON CONFLICT. When the writer's introspection cannot verify it, fall back to a
	// manual DELETE+INSERT script (same statements as before, now dialect-owned).
	public override string BuildStagingMerge(MergeSpec spec)
	{
		if (spec.ConstraintVerified && spec.KeyColumns.Count > 0)
			return base.BuildStagingMerge(spec);

		var join = string.Join(" AND ", spec.KeyColumns.Select(k =>
		{
			var safe = Quote(k);
			return $"{spec.QuotedTargetTable}.{safe} = {spec.SourceTable}.{safe}";
		}));

		var steps = new List<string>();
		if (spec.Mode == MergeMode.Upsert)
			steps.Add($"DELETE FROM {spec.QuotedTargetTable} USING {spec.SourceTable} WHERE {join}");
		else if (spec.Mode == MergeMode.Ignore)
			steps.Add($"DELETE FROM {spec.SourceTable} USING {spec.QuotedTargetTable} WHERE {join}");

		steps.Add($"INSERT INTO {spec.QuotedTargetTable} SELECT * FROM {spec.SourceTable}");
		return string.Join(";", steps);
	}
}
