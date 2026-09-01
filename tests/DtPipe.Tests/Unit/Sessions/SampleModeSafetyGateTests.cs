using DtPipe.Cli.Security;
using DtPipe.Core.Abstractions;
using DtPipe.Sessions;
using AwesomeAssertions;
using Xunit;

namespace DtPipe.Tests.Unit.Sessions;

/// <summary>
/// Neutralising the writer is a claim about the WRITER. A reader can mutate on its way past —
/// DELETE … RETURNING streams rows while the server destroys them, and --limit bounds what the
/// client reads, never what was already deleted. Before this gate the exposure was live: there
/// was exactly one ISqlSafetyPolicy call site in the repository, on execute-yaml-job, so the
/// MCP dry-run tool executed an LLM's query with no SQL check at all.
/// </summary>
public class SampleModeSafetyGateTests
{
	private static readonly ISqlSafetyPolicy Policy = new DefaultSqlSafetyPolicy();

	private static SampleSafetyVerdict Evaluate(ISqlDialect? dialect, params string?[] values)
		=> SampleModeSafetyGate.Evaluate(Policy, new SqlSafetyOptions(), values, dialect);

	[Fact]
	public void A_Destructive_Reader_Query_Is_Refused()
	{
		var verdict = Evaluate(null, "DELETE FROM orders WHERE id < 100 RETURNING *");

		verdict.Allowed.Should().BeFalse(
			"the rows come back as a result set while the server destroys them — cutting the writer changes nothing");
		verdict.Violations.Should().NotBeEmpty();
	}

	[Theory]
	[InlineData("UPDATE orders SET total = 0 OUTPUT inserted.*")]
	[InlineData("TRUNCATE TABLE staging")]
	[InlineData("DROP TABLE audit")]
	[InlineData("ATTACH 'prod.db' AS prod")]
	public void Other_Mutating_Sources_Are_Refused_Too(string sql)
		=> Evaluate(null, sql).Allowed.Should().BeFalse();

	[Fact]
	public void An_Ordinary_Select_Is_Allowed()
		=> Evaluate(null, "SELECT id, name FROM customers ORDER BY id").Allowed.Should().BeTrue();

	[Fact]
	public void Init_Sql_Is_Classified_Like_The_Query()
	{
		var verdict = Evaluate(null, "SELECT * FROM t", "DROP TABLE scratch");

		verdict.Allowed.Should().BeFalse("--duck-init runs arbitrary SQL before the read");
	}

	/// <summary>
	/// The gate covers the source, not the writer. All four hooks are suppressed in sample mode,
	/// so refusing a pipeline for carrying one would add no safety while refusing previews of
	/// ordinary jobs — and a guard that cries wolf teaches people to pass --allow-destructive by
	/// reflex, which would unlock the source side as well.
	/// </summary>
	[Fact]
	public void Writer_Hooks_Are_Not_The_Gates_Business()
	{
		var collected = SampleModeSafetyGate.CollectSqlBearingValues(
			new DtPipe.Core.Options.OptionsRegistry(), readerOptionsType: null);

		collected.Should().BeEmpty();
	}

	[Fact]
	public void A_Server_Enforced_Dialect_Is_Reported_As_Such()
		=> Evaluate(new ReadOnlyCapableDialect(), "SELECT 1").Enforcement
			.Should().Be(ReadOnlyEnforcement.ServerEnforced);

	[Fact]
	public void A_Dialect_Without_A_Read_Only_Session_Reports_The_Weaker_Guarantee()
		=> Evaluate(new NoReadOnlyDialect(), "SELECT 1").Enforcement
			.Should().Be(ReadOnlyEnforcement.VerbScanOnly,
				"a guarantee that is sometimes absent must never be reported as though it were always there");

	[Fact]
	public void The_Real_Dialects_Declare_What_They_Can_Actually_Enforce()
	{
		new DtPipe.Core.Dialects.PostgreSqlDialect().ReadOnlySessionSql.Should().Contain("READ ONLY");
		new DtPipe.Core.Dialects.OracleDialect().ReadOnlySessionSql.Should().Contain("READ ONLY");
		new DtPipe.Core.Dialects.MySqlDialect().ReadOnlySessionSql.Should().Contain("READ ONLY");
		new DtPipe.Core.Dialects.SqliteDialect().ReadOnlySessionSql.Should().Contain("query_only");

		new DtPipe.Core.Dialects.SqlServerDialect().ReadOnlySessionSql.Should().BeNull(
			"ApplicationIntent=ReadOnly routes to a replica; it does not make a session read-only, and claiming otherwise would be the lie this gate exists to prevent");
	}

	private sealed class ReadOnlyCapableDialect : NoReadOnlyDialect
	{
		public override string? ReadOnlySessionSql => "SET TRANSACTION READ ONLY";
	}

	private class NoReadOnlyDialect : ISqlDialect
	{
		public string Normalize(string identifier) => identifier;
		public string Quote(string identifier) => identifier;
		public bool NeedsQuoting(string identifier) => false;
		public virtual string? ReadOnlySessionSql => null;
		public string BuildStagingMerge(MergeSpec spec) => "";
	}
}
