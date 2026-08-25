namespace DtPipe.Core.Abstractions;

using DtPipe.Core.Models;

/// <summary>
/// Defines dialect-specific behaviors for SQL generation, particularly regarding identifier casing and quoting.
/// </summary>
public interface ISqlDialect
{
	/// <summary>
	/// Normalizes an identifier according to the database's default casing rules.
	/// e.g. "MyTable" -> "mytable" (Postgres), "MYTABLE" (Oracle), "MyTable" (SQL Server/SQLite).
	/// </summary>
	string Normalize(string identifier);

	/// <summary>
	/// Quotes an identifier to preserve case and handle special characters.
	/// e.g. "MyTable" -> "\"MyTable\"" (Postgres/Oracle), "[MyTable]" (SQL Server).
	/// </summary>
	string Quote(string identifier);

	/// <summary>
	/// Determines whether an identifier needs quoting based on the dialect's rules and the input string.
	/// Use this to implement "Smart Quoting".
	/// </summary>
	bool NeedsQuoting(string identifier);

	/// <summary>
	/// Gets the SQL query used to discover available tables and views in this dialect's database, or null if unsupported.
	/// </summary>
	string? TableDiscoveryQuery => null;

	/// <summary>
	/// F9 — builds the staged-merge SQL for this dialect from a shared spec.
	/// Base implementation emits ANSI INSERT … ON CONFLICT; dialects override for MERGE
	/// syntax or constraint fallbacks (';'-separated multi-step scripts allowed).
	/// </summary>
	string BuildStagingMerge(MergeSpec spec);

	/// <summary>
	/// F9 — conflict clause for parameterized inserts (SQLite-style VALUES batches).
	/// Empty when unsupported.
	/// </summary>
	string BuildParameterizedConflictClause(IReadOnlyList<string> rawKeys, IReadOnlyList<PipeColumnInfo> columns) => "";
}