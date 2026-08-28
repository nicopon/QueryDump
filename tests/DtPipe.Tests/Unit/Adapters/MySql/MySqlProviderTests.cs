using DtPipe.Adapters.MySql;
using DtPipe.Core.Abstractions;
using Xunit;

namespace DtPipe.Tests.Unit.Adapters.MySql;

/// <summary>
/// Provider-level invariants that hold without a database: selector routing, the deliberate
/// refusal to guess from connection-string content, and the type mappings whose width carries
/// meaning in MySQL.
/// </summary>
public class MySqlProviderTests
{
    // ── Routing ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Server=localhost;Database=db;User ID=root;Password=p")]
    [InlineData("Host=localhost;Database=db")]
    [InlineData("mysql://user:pass@host/db")]
    public void CanHandle_Never_Claims_A_Connection_String_By_Content(string connectionString)
    {
        // A MySQL connection string opens on "Server=…;Database=…", which is exactly what a SQL
        // Server one looks like. Any content heuristic here would steal the other provider's
        // connections; the "mysql:" selector is required and ComponentSelector owns it.
        Assert.False(new MySqlReaderDescriptor().CanHandle(connectionString));
        Assert.False(new MySqlWriterDescriptor().CanHandle(connectionString));
    }

    [Fact]
    public void Selector_Strips_The_Prefix_Before_The_Provider_Sees_It()
    {
        var selection = ComponentSelector.Select("mysql:Server=localhost;Database=db", "mysql");

        Assert.True(selection.Matched);
        Assert.Equal("Server=localhost;Database=db", selection.Cleaned);
        Assert.Null(selection.Variant);
    }

    [Fact]
    public void Selector_Does_Not_Claim_A_Remote_Uri()
    {
        // Guarded catalogue-wide by RemoteUriClaimTests; pinned here for this provider because
        // "mysql://" is a shape users type out of habit from other tools.
        Assert.False(ComponentSelector.Matches("mysql://user:pass@host/db", "mysql"));
    }

    // ── Type mapping ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(int), "INT")]
    [InlineData(typeof(long), "BIGINT")]
    [InlineData(typeof(bool), "TINYINT(1)")]
    [InlineData(typeof(decimal), "DECIMAL(38,9)")]
    [InlineData(typeof(DateTime), "DATETIME(6)")]
    [InlineData(typeof(Guid), "CHAR(36)")]
    [InlineData(typeof(byte[]), "LONGBLOB")]
    [InlineData(typeof(string), "LONGTEXT")]
    public void MapToProviderType_Covers_The_Canonical_Clr_Types(Type clrType, string expected)
        => Assert.Equal(expected, MySqlTypeConverter.Instance.MapToProviderType(clrType));

    [Fact]
    public void DateTimeOffset_Maps_To_Datetime_Not_Timestamp()
    {
        // TIMESTAMP would cap the range at 2038 and reject dates a source can legitimately hold.
        Assert.Equal("DATETIME(6)", MySqlTypeConverter.Instance.MapToProviderType(typeof(DateTimeOffset)));
    }

    [Theory]
    // Width carries meaning, not capacity: these two must be read before the parens are stripped.
    [InlineData("tinyint(1)", typeof(bool))]
    [InlineData("char(36)", typeof(Guid))]
    // …and everything else is ordinary.
    [InlineData("tinyint(4)", typeof(sbyte))]
    [InlineData("tinyint unsigned", typeof(byte))]
    [InlineData("int", typeof(int))]
    [InlineData("int unsigned", typeof(uint))]
    [InlineData("bigint unsigned", typeof(ulong))]
    [InlineData("varchar(255)", typeof(string))]
    [InlineData("longtext", typeof(string))]
    [InlineData("decimal(38,9)", typeof(decimal))]
    [InlineData("datetime(6)", typeof(DateTime))]
    [InlineData("longblob", typeof(byte[]))]
    public void MapFromProviderType_Reads_Column_Type_Not_Data_Type(string columnType, Type expected)
        => Assert.Equal(expected, MySqlTypeConverter.Instance.MapFromProviderType(columnType));

    [Fact]
    public void BuildNativeType_Passes_Through_A_Rendered_Column_Type()
    {
        // COLUMN_TYPE arrives already rendered; re-deriving it from the parts would drop the
        // unsigned attribute that only COLUMN_TYPE carries.
        var mapper = MySqlTypeConverter.Instance;
        Assert.Equal("int unsigned", mapper.BuildNativeType("int unsigned", null, null, null, null));
        Assert.Equal("varchar(255)", mapper.BuildNativeType("varchar(255)", 255, null, null, 255));
        Assert.Equal("decimal(38,9)", mapper.BuildNativeType("decimal", null, 38, 9, null));
    }

    // ── Options contract ─────────────────────────────────────────────────────

    [Fact]
    public void Writer_Options_Expose_Key_For_Upsert_Auto_Detection()
    {
        // --key reaches the writer through IKeyAwareOptions; BaseSqlDataWriter.ResolveKeysAsync
        // prefers the introspected primary key and falls back to this. Both paths must exist for
        // "--strategy Upsert" without "--key" to work.
        var options = new MySqlWriterOptions { Key = "id" };
        Assert.IsAssignableFrom<DtPipe.Core.Options.IKeyAwareOptions>(options);
        Assert.Equal("id", options.Key);
    }
}
