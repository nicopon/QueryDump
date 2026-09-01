using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace DtPipe.Sessions;

/// <summary>
/// Persists a segment's output as encrypted Arrow IPC, and streams it back.
///
/// One frame holds one batch, written as a self-contained mini IPC stream (schema + batch).
/// The schema costs a few hundred bytes per frame against batches of tens of kilobytes, and it
/// buys the property that matters here: reading the first rows decrypts one frame rather than
/// the file, and a frame that fails its tag check does not take the rest of the file with it.
///
/// <b>Ownership</b> (CLAUDE.md › "RecordBatch ownership"):
/// <list type="bullet">
/// <item><see cref="WriteAsync"/> is a terminal consumer — it disposes every batch it is given.</item>
/// <item><see cref="ReadAsync"/> is a producer — the caller owns and disposes what it yields.</item>
/// </list>
/// </summary>
public sealed class CheckpointStore
{
    private const string DataFileName = "data.dtck";

    private readonly SessionStore _session;

    public CheckpointStore(SessionStore session) => _session = session;

    public string PathFor(string checkpointKey) => Path.Combine(_session.CheckpointPath(checkpointKey), DataFileName);

    public bool Contains(string checkpointKey) => File.Exists(PathFor(checkpointKey));

    /// <summary>Checkpoint keys present in this session.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_session.SessionPath)) return System.Array.Empty<string>();
        return Directory.EnumerateDirectories(_session.SessionPath)
            .Where(d => File.Exists(Path.Combine(d, DataFileName)))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Materialises <paramref name="batches"/> under <paramref name="checkpointKey"/>.
    /// Terminal consumer: every batch is disposed here.
    /// </summary>
    /// <returns>Rows written.</returns>
    public async Task<long> WriteAsync(string checkpointKey, IAsyncEnumerable<RecordBatch> batches, CancellationToken ct = default)
    {
        _session.EnsureCreated();
        var key = SessionKeyStore.GetOrCreateKey(_session.Identity.Name);

        var dir = _session.CheckpointPath(checkpointKey);
        Directory.CreateDirectory(dir);

        // Written beside the target then moved into place: a run interrupted mid-write must not
        // leave a half file that reads as a complete checkpoint.
        var finalPath = PathFor(checkpointKey);
        var tempPath = finalPath + ".partial";

        long rows = 0;
        var header = CheckpointCipher.CreateHeader();

        await using (var file = File.Create(tempPath))
        {
            await file.WriteAsync(header, ct);

            long counter = 0;
            await foreach (var batch in batches.WithCancellation(ct))
            {
                using (batch)
                {
                    using var buffer = new MemoryStream();
                    using (var writer = new ArrowStreamWriter(buffer, batch.Schema, leaveOpen: true))
                    {
                        await writer.WriteRecordBatchAsync(batch, ct);
                        await writer.WriteEndAsync(ct);
                    }

                    CheckpointCipher.WriteFrame(file, key, header, counter++, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                    rows += batch.Length;
                }
            }
        }

        File.Move(tempPath, finalPath, overwrite: true);
        return rows;
    }

    /// <summary>
    /// Streams a checkpoint back. The caller owns each batch and disposes it.
    /// </summary>
    public async IAsyncEnumerable<RecordBatch> ReadAsync(string checkpointKey, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = PathFor(checkpointKey);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No checkpoint '{checkpointKey}' in session '{_session.Identity.Name}'.", path);

        var key = SessionKeyStore.TryGetKey(_session.Identity.Name)
            ?? throw new InvalidOperationException(
                $"The key for session '{_session.Identity.Name}' is gone, so its checkpoints are unreadable. " +
                "That is by design: destroying the key is how a purge is made reliable.");

        await using var file = File.OpenRead(path);

        var header = new byte[CheckpointCipher.HeaderSize];
        if (await file.ReadAsync(header, ct) != header.Length)
            throw new InvalidDataException("Checkpoint file is truncated before its header.");
        CheckpointCipher.ValidateHeader(header);

        while (true)
        {
            var frame = CheckpointCipher.ReadFrame(file, key, header);
            if (frame is null) yield break;

            using var buffer = new MemoryStream(frame, writable: false);
            using var reader = new ArrowStreamReader(buffer);
            while (await reader.ReadNextRecordBatchAsync(ct) is { } batch)
                yield return batch;
        }
    }

    /// <summary>The Arrow schema of a stored checkpoint, without streaming its rows.</summary>
    public async Task<Schema?> ReadSchemaAsync(string checkpointKey, CancellationToken ct = default)
    {
        await foreach (var batch in ReadAsync(checkpointKey, ct))
        {
            using (batch) return batch.Schema;
        }
        return null;
    }
}
