using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;
using DtPipe.Core.Options;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Apache.Arrow;
using Apache.Arrow.Types;
using DtPipe.Core.Abstractions.Dag;
using DtPipe.Core.Infrastructure.Arrow;
using DtPipe.Core.Pipelines;
using DtPipe.Configuration;

namespace DtPipe.Services.Pipeline;

// ── Schema phase (P1-8): transformer initialization, segmentation support,
//    branch schema publication and dry-run inference advisory. ──────────────
internal sealed partial class ExportRunState
{
    internal async Task LoadPersistedSchemaAsync(CancellationToken retryCt)
    {
        var readerSchemaPersist = Registry.Get(ReaderFactory.OptionsType) as ISchemaPersistenceAware;
        var schemaLoadName = readerSchemaPersist?.SchemaLoad;
        if (string.IsNullOrEmpty(schemaLoadName)) return;

        var loadedSchema = SchemaStore.Load(schemaLoadName);
        if (loadedSchema != null)
            ExportService.InjectSchema(ReaderFactory, Registry, ArrowSchemaSerializer.SerializeCompact(loadedSchema));
        else
            _svc._logger.LogWarning("Schema file '{Name}' not found — falling back to inference.", schemaLoadName);
    }

    internal async Task InitializeTransformersAsync(CancellationToken retryCt)
    {
        CurrentSchema = Reader.Columns ?? System.Array.Empty<PipeColumnInfo>();
        TransformerSchemas = new Dictionary<IDataTransformer, (IReadOnlyList<PipeColumnInfo> In, IReadOnlyList<PipeColumnInfo> Out)>();

        if (Pipeline.Count == 0) return;

        var transformerNames = Pipeline.Select(t => t.GetType().Name.Replace("DataTransformer", ""));
        if (ShowStatusMessages && !SilenceInternal) Observer.ShowPipeline(transformerNames);

        foreach (var t in Pipeline)
        {
            var inputSchema = CurrentSchema;
            CurrentSchema = await t.InitializeAsync(CurrentSchema, retryCt);
            TransformerSchemas[t] = (inputSchema, CurrentSchema);
        }
    }

    internal void FillSegmentSchemas()
    {
        if (Pipeline.Count == 0) return;
        foreach (var segment in Segments)
        {
            segment.InputSchema = TransformerSchemas.Count > 0 && segment.Transformers.Count > 0
                ? TransformerSchemas[segment.Transformers[0]].In
                : Reader.Columns ?? System.Array.Empty<PipeColumnInfo>();

            segment.OutputSchema = TransformerSchemas.Count > 0 && segment.Transformers.Count > 0
                ? TransformerSchemas[segment.Transformers[^1]].Out
                : CurrentSchema;
        }
    }

    internal void PublishBranchSchema()
    {
        if (string.IsNullOrEmpty(Alias) || _svc._channelRegistry == null || !_svc._channelRegistry.ContainsChannel(Alias))
            return;

        Schema? sourceSchema = (Reader as IStreamTransformer)?.Schema ?? (Reader as IColumnarStreamReader)?.Schema;
        if (!string.IsNullOrEmpty(Alias) && sourceSchema != null)
        {
            var evolvedSchema = ExportService.EvolveSchema(sourceSchema, CurrentSchema);
            _svc._channelRegistry.UpdateArrowChannelSchema(Alias, evolvedSchema);
        }
        _svc._channelRegistry.UpdateChannelColumns(Alias, CurrentSchema ?? System.Array.Empty<PipeColumnInfo>());
    }

    internal async Task InferAdvisoryAsync(CancellationToken retryCt)
    {
        if (Options.DryRunCount <= 0 || Reader is not IColumnTypeInferenceCapable inferCapable) return;
        try
        {
            var sampleCount = Math.Max(Options.DryRunCount, 100);
            var suggested = await inferCapable.InferColumnTypesAsync(sampleCount, retryCt);
            if (suggested.Count > 0 && !SilenceInternal)
                Observer.ShowColumnTypeInferenceSuggestion(suggested, sampleCount);
        }
        catch { /* inference is best-effort, never fail the dry-run */ }
    }

    internal async Task RunDryRunAsync(CancellationToken retryCt)
    {
        IDataWriter? writerForInspection = null;
        if (!string.IsNullOrEmpty(OutputPath))
        {
            try
            {
                if (!string.IsNullOrEmpty(Alias) && _svc._channelRegistry != null && _svc._channelRegistry.ContainsChannel(Alias))
                {
                    if (CurrentSchema != null)
                        _svc._channelRegistry.UpdateChannelColumns(Alias, CurrentSchema);

                    if (Reader is IColumnarStreamReader cr && cr.Schema != null)
                        _svc._channelRegistry.UpdateArrowChannelSchema(Alias, cr.Schema);
                }
                writerForInspection = WriterFactory.Create(Registry);
            }
            catch (Exception ex)
            {
                Observer.LogWarning($"Could not create writer for schema inspection during dry-run: {ex.Message}. Target schema compatibility will not be checked.");
            }
        }

        var executionPlan = ExportService.BuildExecutionPlan(ProviderName, Reader, WriterFactory.ComponentName, writerForInspection, Pipeline, Segments);
        bool isInteractive = string.IsNullOrEmpty(Options.DryRunInteractiveBranch) || string.Equals(Alias, Options.DryRunInteractiveBranch, StringComparison.OrdinalIgnoreCase);
        await Observer.RunDryRunAsync(Reader, Pipeline, Options.DryRunCount, writerForInspection, TransformerSchemas, executionPlan, isInteractive, retryCt);

        if (writerForInspection != null)
            await writerForInspection.DisposeAsync();
    }
}
