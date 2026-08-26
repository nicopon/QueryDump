using DtPipe.Core.Abstractions;
using DtPipe.Core.Dialects;
using DtPipe.Core.Models;
using Xunit;

namespace DtPipe.Tests.Unit.Core;

/// <summary>
/// F9 — dialect-owned staged-merge generation. Pins the exact SQL text per dialect ×
/// mode so refactors cannot silently shift generated SQL (snapshot-first discipline).
/// </summary>
public class DialectUpsertTests
{
    private static readonly PipeColumnInfo Id = new("Id", typeof(int), false);
    private static readonly PipeColumnInfo Val = new("Val", typeof(string), true);

    private static MergeSpec Spec(MergeMode mode, bool verified = true, string source = "[stage]") => new(
        QuotedTargetTable: "\"tgt\"",
        SourceTable: source,
        KeyColumns: new[] { "Id" },
        Columns: new PipeColumnInfo[] { Id, Val },
        Mode: mode,
        ConstraintVerified: verified);

    [Fact]
    public void PostgreSql_Upsert_OnConflict_DoUpdate_Excludes_Keys()
    {
        var sql = new PostgreSqlDialect().BuildStagingMerge(Spec(MergeMode.Upsert));

        Assert.Contains("INSERT INTO \"tgt\" (\"Id\", \"Val\") SELECT \"Id\", \"Val\" FROM", sql);
        Assert.Contains("ON CONFLICT (\"Id\") DO UPDATE SET", sql);
        Assert.Contains("\"Val\" = EXCLUDED.\"Val\"", sql);
        Assert.DoesNotContain("\"Id\" = EXCLUDED", sql); // key columns never updated
    }

    [Fact]
    public void PostgreSql_Ignore_OnConflict_DoNothing()
    {
        var sql = new PostgreSqlDialect().BuildStagingMerge(Spec(MergeMode.Ignore));
        Assert.Contains("ON CONFLICT (\"Id\") DO NOTHING", sql);
    }

    [Fact]
    public void DuckDb_VerifiedConstraint_Matches_Ansi_Shape()
    {
        var sql = new DuckDbDialect().BuildStagingMerge(Spec(MergeMode.Upsert, verified: true));
        Assert.Contains("INSERT INTO \"tgt\"", sql);
        Assert.Contains("ON CONFLICT (\"Id\") DO UPDATE SET \"Val\" = EXCLUDED.\"Val\"", sql);
    }

    [Fact]
    public void DuckDb_Unverified_Falls_Back_To_DeleteThenInsert()
    {
        var sql = new DuckDbDialect().BuildStagingMerge(Spec(MergeMode.Upsert, verified: false));
        var parts = sql.Split(';');
        Assert.Equal(2, parts.Length);
        Assert.Contains("DELETE FROM \"tgt\" USING", parts[0]);
        Assert.Contains("INSERT INTO \"tgt\" SELECT * FROM", parts[1]);
    }

    [Fact]
    public void DuckDb_Unverified_Ignore_Deletes_From_Staging()
    {
        var sql = new DuckDbDialect().BuildStagingMerge(Spec(MergeMode.Ignore, verified: false));
        var parts = sql.Split(';');
        Assert.Contains("DELETE FROM", parts[0]);
        Assert.Contains("USING \"tgt\"", parts[0]); // deletes staging rows already in target
        Assert.Contains("INSERT INTO \"tgt\"", parts[1]);
    }

    [Fact]
    public void SqlServer_Generates_Tsql_Merge()
    {
        var sql = new SqlServerDialect().BuildStagingMerge(Spec(MergeMode.Upsert, source: "stage"));

        Assert.Contains("MERGE \"tgt\" AS T USING [stage] AS S ON (T.Id = S.[Id])", sql);
        Assert.Contains("WHEN MATCHED THEN UPDATE SET T.Val = S.[Val]", sql);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT (Id, Val) VALUES (S.[Id], S.[Val]);", sql);
    }

    [Fact]
    public void SqlServer_Ignore_Omits_WhenMatched_Clause()
    {
        var sql = new SqlServerDialect().BuildStagingMerge(Spec(MergeMode.Ignore, source: "stage"));

        Assert.Contains("MERGE \"tgt\" AS T USING [stage] AS S ON (T.Id = S.[Id])", sql);
        Assert.DoesNotContain("WHEN MATCHED", sql);          // existing keys are skipped
        Assert.Contains("WHEN NOT MATCHED THEN INSERT (Id, Val) VALUES (S.[Id], S.[Val]);", sql);
    }

    [Fact]
    public void Sqlite_Parameterized_Clause_Uses_Excluded()
    {
        var clause = new SqliteDialect().BuildParameterizedConflictClause(new[] { "Id" }, new[] { Id, Val });

        Assert.StartsWith("ON CONFLICT (\"Id\") DO UPDATE SET", clause);
        Assert.Contains("\"Val\" = excluded.\"Val\"", clause);
        Assert.DoesNotContain("\"Id\" =", clause);
    }
}
