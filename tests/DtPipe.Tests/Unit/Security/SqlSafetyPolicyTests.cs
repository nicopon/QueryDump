using System.Collections.Generic;
using DtPipe.Cli.Security;
using Xunit;

namespace DtPipe.Tests.Unit.Security;

/// <summary>
/// F2 — SQL safety policy: destructive verbs and network access are denied by default and only
/// permitted with the matching allow flag (fail-closed).
/// </summary>
public class DefaultSqlSafetyPolicyTests
{
     private readonly DefaultSqlSafetyPolicy _policy = new();

      [Theory]
      [InlineData("DROP TABLE t")]
      [InlineData("DELETE FROM t WHERE 1=1")]
      [InlineData("TRUNCATE t")]
      [InlineData("UPDATE t SET x=1")]
      [InlineData("ALTER TABLE t ADD COLUMN c int")]
      [InlineData("INSERT INTO t VALUES (1)")]
      [InlineData("ATTACH 'x.db' AS x")]
     public void Destructive_Verbs_Are_Blocked_Without_Override(string sql)
        {
         var result = _policy.Analyze(sql, new SqlSafetyOptions());
         Assert.False(result.Allowed);
         Assert.NotEmpty(result.DetectedDestructive);
         Assert.Contains(result.Violations, v => v.Contains("--allow-destructive"));
           }

       [Theory]
       [InlineData("DROP TABLE t")]
       [InlineData("DELETE FROM t")]
       [InlineData("INSERT INTO t VALUES (1)")]
     public void Destructive_Verbs_Are_Allowed_With_AllowDestructive(string sql)
        {
         var result = _policy.Analyze(sql, new SqlSafetyOptions { AllowDestructive = true });
         Assert.True(result.Allowed);
           }

       [Fact]
    public void Select_Queries_Are_Allowed()
        {
         var result = _policy.Analyze("SELECT id, amount FROM sales WHERE amount > 10", new SqlSafetyOptions());
         Assert.True(result.Allowed);
         Assert.Empty(result.DetectedDestructive);
           }

        [Theory]
        [InlineData("LOAD httpfs;")]
        [InlineData("LOAD azure;")]
        [InlineData("SELECT * FROM read_parquet('s3://bucket/data.parquet')")]
        [InlineData("SELECT * FROM read_csv('https://example.com/a.csv')")]
        [InlineData("SELECT * FROM read_json('ftp://host/file.json')")]
       public void Network_Access_Is_Blocked_Without_Override(string sql)
          {
            var result = _policy.Analyze(sql, new SqlSafetyOptions());
             Assert.False(result.Allowed);
             Assert.True(result.NetworkDetected);
             Assert.Contains(result.Violations, v => v.Contains("--allow-network"));
              }

        [Theory]
        [InlineData("LOAD httpfs;")]
        [InlineData("SELECT * FROM read_parquet('s3://bucket/data.parquet')")]
       public void Network_Access_Is_Allowed_With_AllowNetwork(string sql)
          {
             var result = _policy.Analyze(sql, new SqlSafetyOptions { AllowNetwork = true });
             Assert.True(result.Allowed);
              }

         [Fact]
    public void DryRunYaml_Blocks_Destructive_Inside_Yaml()
            {
             var yaml = "main:\n  input: \"pg:host=localhost;Database=prod\"\n  provider-options:\n    pg:\n      pre-exec: \"DROP TABLE IF EXISTS sales\"\n";
             var result = DefaultSqlSafetyPolicy.DryRunYaml(yaml, new SqlSafetyOptions());
             Assert.False(result.Allowed);
             Assert.Contains(result.DetectedDestructive, d => d == "DROP");
              }

         [Fact]
    public void DryRunYaml_Blocks_Network_Inside_Yaml()
            {
             var yaml = "main:\n  input: \"duck:m.db\"\n  provider-options:\n    duck:\n      duck-init: \"LOAD httpfs; SET s3_region='eu'\"\n";
             var result = DefaultSqlSafetyPolicy.DryRunYaml(yaml, new SqlSafetyOptions());
             Assert.False(result.Allowed);
             Assert.True(result.NetworkDetected);
              }

         [Fact]
    public void DryRunYaml_Allows_Pure_Read_Yaml()
              {
               var yaml = "main:\n  input: \"csv:sales.csv\"\n  output: \"parquet:sales.parquet\"\n";
              var result = DefaultSqlSafetyPolicy.DryRunYaml(yaml, new SqlSafetyOptions());
              Assert.True(result.Allowed);
                }
}

/// <summary>
/// F2 — approval gate: a non-interactive context denies a real write by default (fail-closed).
/// </summary>
public class DefaultApprovalGateTests
{
     private readonly DefaultApprovalGate _gate = new();

      [Fact]
    public void NonInteractive_Write_Is_Denied_By_Default()
        {
         var request = new ApprovalRequest { Apply = true, Interactive = false };
        Assert.False(_gate.Approve(request));
           }

       [Fact]
    public void DryRun_Is_Not_A_Write()
        {
         var request = new ApprovalRequest { Apply = false, Interactive = false };
        Assert.False(_gate.Approve(request));
           }

       [Fact]
    public void Interactive_Approves_A_Write()
        {
         var request = new ApprovalRequest { Apply = true, Interactive = true };
        Assert.True(_gate.Approve(request));
           }

        [Fact]
    public void Override_Can_Approve_NonInteractive_Write()
            {
             var gate = new DefaultApprovalGate(_ => true);
             var request = new ApprovalRequest { Apply = true, Interactive = false };
             Assert.True(gate.Approve(request));
               }

         [Fact]
    public void Override_Only_Affects_Writes()
              {
               var gate = new DefaultApprovalGate(_ => false);
               var noApply = new ApprovalRequest { Apply = false, Interactive = true };
               Assert.False(gate.Approve(noApply));
                }
}
