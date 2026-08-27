using System;

namespace DtPipe.Adapters.DuckDB;

public static class DuckDbConnectionHelper
{
	/// <summary>ADO connection string for an ephemeral in-memory DuckDB instance.</summary>
	public const string InMemoryConnectionString = "Data Source=:memory:;";

	/// <summary>True for every accepted spelling of "run in memory".</summary>
	private static bool IsInMemory(string path) =>
		string.IsNullOrEmpty(path)
		|| path.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
		|| path.Equals("memory", StringComparison.OrdinalIgnoreCase)
		|| path.Equals(":memory", StringComparison.OrdinalIgnoreCase);

	public static bool CanHandle(string connectionString)
	{
		if (string.IsNullOrWhiteSpace(connectionString)) return false;

		return connectionString.EndsWith(".duckdb", StringComparison.OrdinalIgnoreCase);
	}

	public static string GetConnectionString(string connectionString)
	{
		if (string.IsNullOrWhiteSpace(connectionString)) return InMemoryConnectionString;

		if (connectionString.StartsWith("duck+", StringComparison.OrdinalIgnoreCase))
		{
			return connectionString;
		}

		if (connectionString.StartsWith("duck:", StringComparison.OrdinalIgnoreCase))
		{
			var path = connectionString.Substring(5).Trim();

			// The in-memory spellings must map to DuckDB's ":memory:" sentinel. Stripping a
			// leading colon first turned "duck::memory:" into "memory:" and left "duck:memory"
			// as "memory", so both documented forms silently created a database FILE named
			// "memory" in the working directory instead of running in memory.
			if (IsInMemory(path)) return InMemoryConnectionString;

			if (path.StartsWith(":")) path = path.Substring(1);
			return string.IsNullOrEmpty(path) ? InMemoryConnectionString : $"Data Source={path};";
		}

		if (!connectionString.Contains('=', StringComparison.OrdinalIgnoreCase))
		{
			return $"Data Source={connectionString};";
		}

		return connectionString;
	}
}
