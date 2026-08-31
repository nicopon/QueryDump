using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;
using DtPipe.Core.Options;
using DtPipe.Core.Validation;
using Microsoft.Extensions.Logging;

namespace DtPipe.Services;

/// <summary>
/// Validates schema compatibility and performs auto-migration if enabled.
/// </summary>
public sealed class SchemaValidationService
{
    private readonly IExportObserver _observer;
    private readonly ILogger<SchemaValidationService> _logger;

    public SchemaValidationService(IExportObserver observer, ILogger<SchemaValidationService> logger)
    {
        _observer = observer;
        _logger = logger;
    }

    public async Task ValidateAndMigrateAsync(
        IDataWriter writer,
        IReadOnlyList<PipeColumnInfo> exportableSchema,
        ISchemaValidationAware? settings,
        CancellationToken ct)
    {
        if (settings?.NoSchemaValidation == true || writer is not ISchemaInspector inspector) return;

        if (!inspector.RequiresTargetInspection)
        {
            _logger.LogDebug("Target inspection skipped for {WriterType} (not required by current strategy).", writer.GetType().Name);
            return;
        }

        _observer.LogMessage("Verifying target schema compatibility...");
        var targetSchema = await inspector.InspectTargetAsync(ct);
        var dialect = (writer as IHasSqlDialect)?.Dialect;
        var report = SchemaCompatibilityAnalyzer.Analyze(exportableSchema, targetSchema, dialect);

        var missingCount = report.Columns.Count(c => c.Status == CompatibilityStatus.MissingInTarget);
        var willMigrate = missingCount > 0 && settings?.AutoMigrate == true && writer is ISchemaMigrator;

        foreach (var warning in report.Warnings) _observer.LogWarning(warning);

        // A missing column is an error only until auto-migration adds it, and its message claims
        // the data will be skipped — both wrong on a run that is about to migrate and succeed.
        // The "Auto-migrating schema" line below states what actually happens.
        foreach (var error in report.Errors)
        {
            if (willMigrate && report.MissingColumnErrors.Contains(error)) continue;
            _observer.LogError(new Exception(error));
        }

        if (report.IsCompatible)
        {
            _observer.LogMessage("Target schema compatible.");
        }

        if (willMigrate && writer is ISchemaMigrator migrator)
        {
            _observer.LogMessage($"[yellow]Auto-migrating schema: Adding {missingCount} missing columns...[/]");
            await migrator.MigrateSchemaAsync(report, ct);

            targetSchema = await inspector.InspectTargetAsync(ct);
            report = SchemaCompatibilityAnalyzer.Analyze(exportableSchema, targetSchema, dialect);

            if (!report.IsCompatible && settings?.StrictSchema == true)
                throw new InvalidOperationException("Export aborted: Schema migration failed to resolve all incompatibilities in Strict Mode.");

            _observer.LogMessage("[green]Schema migration successful. Continuing export.[/]");
        }
        else if (!report.IsCompatible && settings?.StrictSchema == true)
        {
            throw new InvalidOperationException("Export aborted due to schema incompatibilities (Strict Mode).");
        }
    }
}
