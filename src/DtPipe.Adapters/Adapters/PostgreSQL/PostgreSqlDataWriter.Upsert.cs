using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apache.Arrow;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DtPipe.Adapters.PostgreSQL;

public sealed partial class PostgreSqlDataWriter
{
	private async Task WriteBatchViaStagingAsync(IReadOnlyList<object?[]> rows, CancellationToken ct)
	{
		var stagingTable = $"tmp_batch_{Guid.NewGuid():N}";
		await ExecuteNonQueryAsync($"CREATE TEMP TABLE {stagingTable} (LIKE {_quotedTargetTableName} INCLUDING DEFAULTS)", ct);

		try
		{
			var copySql = BuildCopySql(stagingTable);
			await using (var writer = await ((NpgsqlConnection)_connection!).BeginBinaryImportAsync(copySql, ct))
			{
				await WriteRowsToCopyAsync(writer, rows, ct);
				await writer.CompleteAsync(ct);
			}

			await MergeStagingBatchAsync(stagingTable, ct);
		}
		finally
		{
			try
			{
				await ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {stagingTable}", ct);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to drop staging table {TableName}", stagingTable);
			}
		}
	}

	private async Task WriteRecordBatchViaStagingAsync(RecordBatch batch, CancellationToken ct)
	{
		var stagingTable = $"tmp_batch_{Guid.NewGuid():N}";
		await ExecuteNonQueryAsync($"CREATE TEMP TABLE {stagingTable} (LIKE {_quotedTargetTableName} INCLUDING DEFAULTS)", ct);

		try
		{
			var copySql = BuildCopySql(stagingTable);
			await using (var writer = await ((NpgsqlConnection)_connection!).BeginBinaryImportAsync(copySql, ct))
			{
				await WriteColumnarToCopyAsync(writer, batch, ct);
				await writer.CompleteAsync(ct);
			}

			await MergeStagingBatchAsync(stagingTable, ct);
		}
		finally
		{
			try { await ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {stagingTable}", ct); } catch { }
		}
	}

	private async Task MergeStagingBatchAsync(string stagingTable, CancellationToken ct)
	{
		var cols = _targetNames!.Select(n => _dialect.Quote(n)).ToList();
		var conflictTarget = string.Join(", ", _keyColumns.Select(c => _dialect.Quote(c)));
		var quotedStaging = _dialect.Quote(stagingTable);

		var updateSet = string.Join(", ",
			_targetNames!.Where(n => !_keyColumns.Contains(n, StringComparer.OrdinalIgnoreCase))
						.Select(n => $"{_dialect.Quote(n)} = EXCLUDED.{_dialect.Quote(n)}"));

		var sb = new StringBuilder();
		sb.Append($"INSERT INTO {_quotedTargetTableName} ({string.Join(", ", cols)}) SELECT {string.Join(", ", cols)} FROM {quotedStaging} ");

		if (_options.Strategy == PostgreSqlWriteStrategy.Ignore)
		{
			sb.Append($"ON CONFLICT ({conflictTarget}) DO NOTHING");
		}
		else if (_options.Strategy == PostgreSqlWriteStrategy.Upsert)
		{
			sb.Append($"ON CONFLICT ({conflictTarget}) DO UPDATE SET {updateSet}");
		}

		await ExecuteNonQueryAsync(sb.ToString(), ct);
	}
}
