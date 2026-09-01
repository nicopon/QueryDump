using System.Threading.Channels;
using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;
using DtPipe.Core.Options;
using Microsoft.Extensions.Logging;
using Apache.Arrow;
using DtPipe.Core.Infrastructure.Arrow;
using DtPipe.Core.Pipelines;
using DtPipe.Services;

namespace DtPipe.Services.Pipeline;

// ── Execution phase (P1-8): writer preparation (cursor decorators, schema validation,
//    pre-hook, progress) and the segmented pipeline execution with hooks/metrics. ──
internal sealed partial class ExportRunState
{
    internal async Task PrepareWriterAsync(CancellationToken retryCt)
    {
        if (ShowStatusMessages && !SilenceInternal)
            Observer.ShowTarget(WriterFactory.ComponentName, OutputPath);

        var exportableSchema = CurrentSchema ?? throw new InvalidOperationException("Exportable schema is null.");
        // Constructing a writer does not touch its target — DryRunSafeWriterTests holds that
        // invariant for the catalogue — so a sample run can build the real writer and inspect it.
        Writer = WriterFactory.Create(Registry);

        if (IsSampleMode)
        {
            await PrepareSampleWriterAsync(exportableSchema, retryCt);
            return;
        }

        // Cursor tracking: wrap the writer if --cursor is specified
        CursorTracker = null;
        EffectiveWriter = Writer;
        if (!string.IsNullOrEmpty(Options.Cursor) && !string.IsNullOrEmpty(Options.State))
        {
            if (Writer is IColumnarDataWriter columnar)
            {
                var colDecorator = new DtPipe.Core.Cursor.CursorTrackingColumnarDecorator(columnar, Options.Cursor);
                CursorTracker = colDecorator;
                EffectiveWriter = colDecorator;
            }
            else
            {
                var rowDecorator = new DtPipe.Core.Cursor.CursorTrackingRowDecorator(Writer, Options.Cursor);
                CursorTracker = rowDecorator;
                EffectiveWriter = rowDecorator;
            }

            var currentCursor = DtPipe.Core.Cursor.CursorStateStore.Read(Options.State);
            if (currentCursor != null && !SilenceInternal)
                Observer.LogMessage($"[grey]   Cursor loaded: {currentCursor.Column} = {currentCursor.Value} (from {Options.State})[/]");
            else if (!SilenceInternal)
                Observer.LogMessage($"[grey]   No active cursor state found at {Options.State}[/]");
        }

        // Read schema validation and hook settings from writer options
        var writerSchemaSettings = Registry.Get(WriterFactory.OptionsType) as ISchemaValidationAware;
        WriterHooks = Registry.Get(WriterFactory.OptionsType) as IHookAware;

        // Schema Validation
        await _svc._schemaValidator.ValidateAndMigrateAsync(Writer, exportableSchema, writerSchemaSettings, retryCt);

        // Execute Pre-Hook (from writer options)
        await _svc._hookExecutor.ExecuteAsync(Writer, "Pre-Hook", WriterHooks?.PreExec, retryCt);

        await EffectiveWriter.InitializeAsync(exportableSchema, retryCt);

        Progress = CreateProgress();

        LinkedCts = CancellationTokenSource.CreateLinkedTokenSource(retryCt);

        PropagateSegmentArrowSchemas();
    }

    /// <summary>
    /// Writer preparation for a sample run. Same reader, same transformers, same segmentation —
    /// only the writer boundary differs, and three side effects the ordinary path performs are
    /// deliberately absent:
    ///
    /// <list type="bullet">
    /// <item><b>No schema migration.</b> ValidateAndMigrateAsync can CREATE or ALTER the target
    /// table. The target is inspected instead, which is what feeds the compatibility report.</item>
    /// <item><b>No hooks.</b> Every hook is SQL run on the target connection
    /// (HookExecutor → writer.ExecuteCommandAsync), so a pre-hook is typically
    /// "TRUNCATE TABLE target". None of the four runs here — including on-error, which has no
    /// exception: a rule with an exception is not a rule that can be tested.</item>
    /// <item><b>No cursor decorator.</b> Nothing is written, so there is no watermark to advance.</item>
    /// </list>
    ///
    /// InitializeAsync is called on the SINK, never on the real writer: for a file writer that
    /// call is what creates the file.
    /// </summary>
    private async Task PrepareSampleWriterAsync(IReadOnlyList<PipeColumnInfo> exportableSchema, CancellationToken retryCt)
    {
        CursorTracker = null;
        EffectiveWriter = SampleModeSink.Wrap(Writer);

        if (Writer is ISchemaInspector inspector)
        {
            try
            {
                InspectedTarget = await inspector.InspectTargetAsync(retryCt);
            }
            catch (Exception ex)
            {
                // Inspection is advisory: an unreachable target must not fail the preview.
                TargetInspectionError = ex.Message;
                Logger.LogDebug(ex, "[Sample] target inspection failed");
            }
        }

        await EffectiveWriter.InitializeAsync(exportableSchema, retryCt);

        SampleTap = new DtPipe.DryRun.SampleTapRecorder(Options.DryRunCount);
        DeclareSampleStages();

        Progress = CreateProgress();
        LinkedCts = CancellationTokenSource.CreateLinkedTokenSource(retryCt);

        PropagateSegmentArrowSchemas();
    }

    /// <summary>
    /// Names the stages the tap will be offered rows for: 0 is the reader, then the transformers
    /// in pipeline order, each with the schema its InitializeAsync produced and the mode of the
    /// segment it belongs to.
    /// </summary>
    private void DeclareSampleStages()
    {
        if (SampleTap is null) return;

        SampleTap.OnStageSchema(0, ProviderName,
            Reader.Columns ?? System.Array.Empty<PipeColumnInfo>(),
            Reader is IColumnarStreamReader);

        var columnarByTransformer = Segments
            .SelectMany(seg => seg.Transformers.Select(t => (t, seg.IsColumnar)))
            .ToDictionary(x => x.t, x => x.IsColumnar);

        for (var i = 0; i < Pipeline.Count; i++)
        {
            var t = Pipeline[i];
            var outSchema = TransformerSchemas.TryGetValue(t, out var s) ? s.Out : CurrentSchema;
            SampleTap.OnStageSchema(
                i + 1,
                t.GetType().Name.Replace("DataTransformer", ""),
                outSchema,
                columnarByTransformer.TryGetValue(t, out var isCol) && isCol);
        }
    }

    /// <summary>
    /// Propagates InputSchemaArrow for each segment so the row→columnar bridge can preserve complex
    /// Arrow type metadata (Timestamp timezone, Decimal precision/scale, arrow.uuid annotations).
    /// </summary>
    private void PropagateSegmentArrowSchemas()
    {
        Schema? readerArrowSchema = (Reader as IStreamTransformer)?.Schema ?? (Reader as IColumnarStreamReader)?.Schema;
        foreach (var segment in Segments)
        {
            segment.InputSchemaArrow = readerArrowSchema != null
                ? ArrowSchemaFactory.CreateEnriched(segment.InputSchema, readerArrowSchema)
                : ArrowSchemaFactory.Create(segment.InputSchema);
        }
    }

    private IExportProgress CreateProgress()
    {
        var transformerModes = Segments
            .SelectMany(s => s.Transformers.Select(t => (
                Name: t.GetType().Name.Replace("DataTransformer", ""),
                IsColumnar: s.IsColumnar)))
            .ToList();

        return (SilenceInternal && ResultsCollector == null)
            ? (IExportProgress)new DtPipe.Feedback.NullExportProgress()
            : Observer.CreateProgressReporter(
                !Options.NoStats && !SilenceInternal,
                transformerModes,
                suppressLiveTui: OutputIsStdio || SilenceInternal,
                branchName: Alias,
                suppressCompletionOutput: ResultsCollector != null);
    }

    internal async Task ExecutePipelineAsync(CancellationToken retryCt)
    {
        var exportableSchema = CurrentSchema ?? throw new InvalidOperationException("Exportable schema is null.");
        var effectiveCt = LinkedCts.Token;
        var startTime = DateTime.UtcNow;

        try
        {
            Logger.LogDebug("[Execution] running segmented pipeline");
            var effectiveOptions = IsSampleMode ? Options with { Limit = EffectiveLimit } : Options;
            await _svc._pipelineExecutor.ExecuteSegmentedPipelineAsync(
                Reader, EffectiveWriter, Segments, exportableSchema, effectiveOptions, Progress, LinkedCts, effectiveCt, SampleTap);

            if (IsSampleMode)
            {
                // Nothing was written, so nothing is completed, hooked, tracked or measured.
                // CompleteAsync on a real writer is what flushes a Parquet footer or commits a
                // transaction — the sink's is a no-op and the real writer never sees one.
                SampleResult = SampleTap!.Build(
                    Progress.GetMetrics().ReadCount,
                    (EffectiveWriter as ISampleModeSink)?.RowsWritten ?? 0);
                Progress.Complete();
                return;
            }

            await Writer.CompleteAsync(retryCt);
            Logger.LogDebug("[Cursor] persisting state if tracked");

            // Persist cursor state after successful CompleteAsync
            if (CursorTracker?.TrackedMaxValue != null && !string.IsNullOrEmpty(Options.State))
            {
                var runMeta = new DtPipe.Core.Cursor.CursorRunMetadata(
                    StartedAt: startTime,
                    CompletedAt: DateTime.UtcNow,
                    RowsTransferred: Progress.GetMetrics().WriteCount,
                    Status: "success");
                DtPipe.Core.Cursor.CursorStateStore.Save(Options.State, CursorTracker.TrackedMaxValue, runMeta);
                if (!SilenceInternal)
                    Observer.LogMessage($"[grey]   Cursor saved: {Options.Cursor} = {CursorTracker.TrackedMaxValue.Value} → {Options.State}[/]");
            }

            Progress.Complete();

            ResultsCollector?.Enqueue(new DtPipe.Feedback.BranchSummary(
                Alias,
                Progress.GetMetrics(),
                Reader is DtPipe.Core.Abstractions.IColumnarStreamReader,
                GetTransformerModes()));

            var elapsed = DateTime.UtcNow - startTime;
            var rowsPerSecond = elapsed.TotalSeconds > 0 ? Progress.GetMetrics().ReadCount / elapsed.TotalSeconds : 0;
            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInformation("Export completed in {Elapsed}. Written {Rows} rows ({Speed:F1} rows/s).", elapsed, Progress.GetMetrics().WriteCount, rowsPerSecond);

            // ── POST-EXEC HOOK + METRICS ──
            await _svc._hookExecutor.ExecuteAsync(Writer, "Post-Hook", WriterHooks?.PostExec, retryCt);
            Logger.LogDebug("[Metrics] saving run metrics");
            await _svc._metricsService.SaveMetricsAsync(Progress, Options.MetricsPath, retryCt);
        }
        catch (OperationCanceledException)
        {
            // F16: cancellation must never mask as success. Re-throw so the caller can
            // discriminate user-initiated shutdown (exit code 130) from internal
            // cancellation. Orphaned producers stay a normal event: they are absorbed
            // by DagOrchestrator.ExecuteBranchAsync's documented orphaned-producer path.
            Progress.Complete();
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Export failed");
            Observer.LogError(ex);

            try
            {
                if (!IsSampleMode)
                    await _svc._hookExecutor.ExecuteAsync(Writer, "On-Error Hook", WriterHooks?.OnErrorExec, CancellationToken.None, TimeSpan.FromSeconds(ExportService.HookTimeoutSeconds));
            }
            catch (Exception hookEx)
            {
                Logger.LogError(hookEx, "On-Error Hook failed");
                Observer.LogError(hookEx);
            }

            throw;
        }
        finally
        {
            try
            {
                if (!IsSampleMode)
                    await _svc._hookExecutor.ExecuteAsync(Writer, "Finally Hook", WriterHooks?.FinallyExec, CancellationToken.None, TimeSpan.FromSeconds(ExportService.HookTimeoutSeconds));
            }
            catch (Exception hookEx)
            {
                Logger.LogError(hookEx, "Finally Hook failed");
                Observer.LogError(hookEx);
            }
        }
    }

    private List<(string Name, bool IsColumnar)> GetTransformerModes()
        => Segments
            .SelectMany(s => s.Transformers.Select(t => (
                Name: t.GetType().Name.Replace("DataTransformer", ""),
                IsColumnar: s.IsColumnar)))
            .ToList();
}
