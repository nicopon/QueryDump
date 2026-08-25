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
        Writer = WriterFactory.Create(Registry);

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

        // Propagate InputSchemaArrow for each segment so the row→columnar bridge can preserve complex
        // Arrow type metadata (Timestamp timezone, Decimal precision/scale, arrow.uuid annotations).
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
            await _svc._pipelineExecutor.ExecuteSegmentedPipelineAsync(Reader, EffectiveWriter, Segments, exportableSchema, Options, Progress, LinkedCts, effectiveCt);

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
