using System.ComponentModel;
using DtPipe.Adapters.Common;
using DtPipe.Core.Attributes;
using DtPipe.Core.Options;

namespace DtPipe.Adapters.PostgreSQL;

[Description("Writes data to a PostgreSQL database.")]
[ComponentHelp(
	usageNotes: "Connection string format: 'pg:Host=host;Port=port;Database=db;Username=user;Password=pass'. In YAML, use 'provider-options' -> 'pg' (or 'pg-writer' when the same job also reads from PostgreSQL) to set table, strategy, and insert mode.",
	examples: new[] {
		"main:\n  input: \"orders.parquet\"\n  output: \"pg:Host=localhost;Database=prod;Username=postgres\"\n  provider-options:\n    pg-writer:\n      table: \"public.orders\"\n      strategy: \"Upsert\"\n      key: \"order_id\""
	})]
public class PostgreSqlWriterOptions : DbWriterOptions, IWriterOptions
{
	public static string Prefix => PostgreSqlConstants.ProviderName;
	public static string DisplayName => "PostgreSQL Writer Options";

	[ComponentOption("--table", Aliases = new[] { "-t" }, Description = "Target table name", Required = true)]
	public string Table { get; set; } = string.Empty;

	[ComponentOption("--strategy", Aliases = new[] { "-s" }, Description = "Write strategy: Append, Truncate, or DeleteThenInsert", Hidden = true)]
	public PostgreSqlWriteStrategy? Strategy { get; set; }

	[ComponentOption("--insert-mode", Description = "Data insert mode (Standard, Bulk)", Hidden = true)]
	public PostgreSqlInsertMode? InsertMode { get; set; }
}

public enum PostgreSqlInsertMode
{
	Standard,
	Bulk
}

public enum PostgreSqlWriteStrategy
{
	Append,
	Truncate,
	DeleteThenInsert,
	Recreate,
	Upsert,
	Ignore
}
