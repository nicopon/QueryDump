using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;
using DtPipe.Sessions;
using DtPipe.Services;
using DtPipe.Tests.Helpers;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DtPipe.Tests.Integration;

/// <summary>
/// A checkpoint has to be able to stand in for the source it was taken from. These drive the
/// real executor: what a run writes with a checkpoint must equal what it writes without one,
/// and resuming from the checkpoint must replay the same rows.
/// </summary>
public class CheckpointRoundTripTests : IDisposable
{
	private readonly string _tmp;
	private readonly string? _savedState;
	private readonly SessionStore _session;
	private readonly CheckpointStore _store;

	public CheckpointRoundTripTests()
	{
		_tmp = Path.Combine(Path.GetTempPath(), $"dtpipe_rt_{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tmp);
		_savedState = Environment.GetEnvironmentVariable(UserStatePaths.RootEnvironmentVariable);
		Environment.SetEnvironmentVariable(UserStatePaths.RootEnvironmentVariable, Path.Combine(_tmp, "state"));

		_session = new SessionStore(new SessionIdentity("rt", Path.Combine(_tmp, ".dtpipe"), SessionOrigin.Explicit));
		_store = new CheckpointStore(_session);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(UserStatePaths.RootEnvironmentVariable, _savedState);
		if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
	}

	private static readonly PipelineExecutor Executor = new(
		[new DtPipe.Adapters.Infrastructure.Arrow.ArrowRowToColumnarBridgeFactory(
			NullLogger<DtPipe.Core.Infrastructure.Arrow.ArrowRowToColumnarBridge>.Instance)],
		[new DtPipe.Adapters.Infrastructure.Arrow.ArrowColumnarToRowBridgeFactory()],
		NullLogger<PipelineExecutor>.Instance);

	[Fact]
	public async Task A_Tee_Does_Not_Change_What_The_Writer_Receives()
	{
		var without = await RunAsync(checkpointKey: null);
		var with = await RunAsync(checkpointKey: "k1");

		with.Should().Equal(without, "materialising is observation, not transformation");
	}

	[Fact]
	public async Task Resuming_From_A_Checkpoint_Replays_The_Same_Rows()
	{
		var original = await RunAsync(checkpointKey: "k1");

		var resumed = new List<int>();
		var reader = new CheckpointStreamReader(_store, "k1");
		await reader.OpenAsync();
		await foreach (var batch in reader.ReadRecordBatchesAsync())
		{
			using (batch)
			{
				var ids = (Int32Array)batch.Column(0);
				for (var i = 0; i < batch.Length; i++) resumed.Add(ids.GetValue(i)!.Value);
			}
		}

		resumed.Should().Equal(original);
	}

	[Fact]
	public async Task A_Checkpoint_Holds_What_The_Writer_Would_Have_Received()
	{
		// Not the source rows: the transformer runs before the materialisation point, so
		// resuming skips work already done rather than redoing it.
		await RunAsync(checkpointKey: "k1", transform: true);

		var rows = new List<int>();
		await foreach (var batch in _store.ReadAsync("k1"))
		{
			using (batch)
			{
				var ids = (Int32Array)batch.Column(0);
				for (var i = 0; i < batch.Length; i++) rows.Add(ids.GetValue(i)!.Value);
			}
		}

		rows.Should().Equal(Enumerable.Range(0, 8).Select(i => i + 1000));
	}

	[Fact]
	public async Task Teeing_Leaks_No_Native_Memory()
	{
		var pool = new TrackingMemoryPool();

		await RunAsync(checkpointKey: "k1", pool: pool);

		pool.TotalAllocations.Should().BeGreaterThan(0);
		pool.ActiveAllocations.Should().Be(0,
			"the tee retains a reference per extra consumer and each disposes its own — a refcount bump, never a deep copy");
	}

	[Fact]
	public async Task An_Interrupted_Stream_Publishes_No_Checkpoint()
	{
		var act = async () => await RunAsync(checkpointKey: "k1", failAfter: 1);

		await act.Should().ThrowAsync<InvalidOperationException>();
		_store.Contains("k1").Should().BeFalse(
			"a truncated checkpoint that looked complete would be replayed as if it were the whole source");
	}

	[Fact]
	public void Resuming_From_A_Missing_Checkpoint_Names_What_Is_Available()
	{
		var factory = new CheckpointReaderFactory("nope", "rt");

		var act = () => factory.Create(new DtPipe.Core.Options.OptionsRegistry());

		act.Should().Throw<InvalidOperationException>().WithMessage("*no checkpoints*");
	}

	[Fact]
	public void A_Checkpoint_Key_Is_Never_Claimed_By_Connection_String_Matching()
		=> new CheckpointReaderFactory("abc", null).CanHandle("abc").Should().BeFalse(
			"a hex key must never enter the component-prefix grammar");

	// ─────────────────────────────────────────────────────────────────────────

	private async Task<List<int>> RunAsync(
		string? checkpointKey, bool transform = false, TrackingMemoryPool? pool = null, int failAfter = -1)
	{
		var columns = new List<PipeColumnInfo> { new("Id", typeof(int), false) };
		var reader = new BatchReader(8, pool, failAfter);
		await reader.OpenAsync();

		var pipeline = transform ? new List<IDataTransformer> { new Offset(1000) } : [];
		var schema = columns;
		foreach (var t in pipeline) schema = (List<PipeColumnInfo>)await t.InitializeAsync(schema);

		var segments = DtPipe.Core.Pipelines.PipelineSegmenter.GetSegments(pipeline);
		foreach (var s in segments) { s.InputSchema = columns; s.OutputSchema = schema; }

		var writer = new CollectingColumnarWriter();
		Func<IAsyncEnumerable<RecordBatch>, IAsyncEnumerable<RecordBatch>>? materialise =
			checkpointKey is null ? null : src => CheckpointTee.TeeAsync(src, _store, checkpointKey);

		using var cts = new CancellationTokenSource();
		await Executor.ExecuteSegmentedPipelineAsync(
			reader, writer, segments, schema, new PipelineOptions { BatchSize = 3 },
			Mock.Of<IExportProgress>(), cts, cts.Token, null, materialise);

		return writer.Ids;
	}

	private sealed class Offset : BaseColumnarTransformer
	{
		private readonly int _by;
		public Offset(int by) => _by = by;
		public override bool CanProcessColumnar => true;
		protected override ValueTask<RecordBatch?> TransformBatchSafeAsync(RecordBatch batch, CancellationToken ct = default)
		{
			var src = (Int32Array)batch.Column(0);
			var b = new Int32Array.Builder();
			for (var i = 0; i < batch.Length; i++) b.Append(src.GetValue(i)!.Value + _by);
			return ValueTask.FromResult<RecordBatch?>(new RecordBatch(batch.Schema, new IArrowArray[] { b.Build() }, batch.Length));
		}
	}

	private sealed class BatchReader : IColumnarStreamReader
	{
		private readonly int _n;
		private readonly TrackingMemoryPool? _pool;
		private readonly int _failAfter;
		private readonly Schema _schema = new Schema.Builder().Field(f => f.Name("Id").DataType(Int32Type.Default).Nullable(false)).Build();

		public BatchReader(int n, TrackingMemoryPool? pool, int failAfter) { _n = n; _pool = pool; _failAfter = failAfter; }

		public IReadOnlyList<PipeColumnInfo>? Columns => new List<PipeColumnInfo> { new("Id", typeof(int), false) };
		public Schema? Schema => _schema;
		public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;

		public async IAsyncEnumerable<RecordBatch> ReadRecordBatchesAsync([EnumeratorCancellation] CancellationToken ct = default)
		{
			var emitted = 0;
			for (var i = 0; i < _n; i += 3)
			{
				if (_failAfter >= 0 && emitted >= _failAfter) throw new InvalidOperationException("source failed");
				var count = Math.Min(3, _n - i);
				var b = new Int32Array.Builder();
				for (var j = 0; j < count; j++) b.Append(i + j);
				yield return new RecordBatch(_schema, [_pool is null ? b.Build() : b.Build(_pool)], count);
				emitted++;
				await Task.Yield();
			}
		}

		public async IAsyncEnumerable<ReadOnlyMemory<object?[]>> ReadBatchesAsync(int batchSize, [EnumeratorCancellation] CancellationToken ct = default)
		{
			await foreach (var batch in ReadRecordBatchesAsync(ct))
				using (batch)
					foreach (var m in DtPipe.Core.Infrastructure.Arrow.ArrowRowConverter.FlattenBatch(batch, batchSize))
						yield return m;
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class CollectingColumnarWriter : IColumnarDataWriter
	{
		public List<int> Ids { get; } = new();
		public ValueTask InitializeAsync(IReadOnlyList<PipeColumnInfo> c, CancellationToken ct = default) => ValueTask.CompletedTask;
		public ValueTask WriteRecordBatchAsync(RecordBatch batch, CancellationToken ct = default)
		{
			using (batch)
			{
				var ids = (Int32Array)batch.Column(0);
				for (var i = 0; i < batch.Length; i++) Ids.Add(ids.GetValue(i)!.Value);
			}
			return ValueTask.CompletedTask;
		}
		public ValueTask CompleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
		public ValueTask ExecuteCommandAsync(string command, CancellationToken ct = default) => ValueTask.CompletedTask;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
