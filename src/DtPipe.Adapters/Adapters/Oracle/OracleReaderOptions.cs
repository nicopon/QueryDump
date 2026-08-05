using System.ComponentModel;
using DtPipe.Core.Attributes;
using DtPipe.Adapters.Common;
using DtPipe.Core.Options;

namespace DtPipe.Adapters.Oracle;

[Description("Reads data from an Oracle database.")]
[ComponentHelp(
	usageNotes: "Connection string format: 'ora:Data Source=host:port/service_name;User Id=user;Password=pass'. In YAML, use 'provider-options' -> 'ora' to set query, table, or fetch-size.",
	examples: new[] {
		"main:\n  input: \"ora:Data Source=PROD:1521/orcl;User Id=scott;Password=tiger\"\n  provider-options:\n    ora:\n      query: \"SELECT * FROM sales.orders\"\n  output: \"parquet:orders.parquet\""
	})]
public class OracleReaderOptions : QueryableReaderOptions, IProviderOptions
{
	public static string Prefix => OracleConstants.ProviderName;
	public static string DisplayName => "Oracle Reader Options";

	[Description("Fetch size in bytes (Oracle only)")]
	public int FetchSize { get; set; } = 1_048_576;
}
