using DtPipe.Core.Abstractions;
using DtPipe.Core.Models;
using DtPipe.Core.Validation;

namespace DtPipe.DryRun;

/// <summary>
/// Everything a sample run has to show: what it observed, and what that implies about the
/// target. Assembled after the run, from the run — there is no second pass over the data.
/// </summary>
public sealed record SampleReport(
	SampleRun Run,
	List<SampleTrace> Samples,
	List<string> StepNames,
	SchemaCompatibilityReport? CompatibilityReport,
	string? SchemaInspectionError,
	ISqlDialect? Dialect = null,
	KeyValidationResult? KeyValidation = null,
	ConstraintValidationResult? ConstraintValidation = null,
	IReadOnlyDictionary<string, string>? PerformanceHints = null,
	/// <summary>
	/// What the run could actually guarantee about not writing. "No data written" is a claim
	/// about the writer; the source is a separate question, and a report must not answer one
	/// with the other. A verb scan does not prove a query is read-only — SELECT my_function()
	/// passes it — so the promise stops where the proof does.
	/// </summary>
	DtPipe.Sessions.ReadOnlyEnforcement Enforcement = DtPipe.Sessions.ReadOnlyEnforcement.VerbScanOnly,
	/// <summary>Which branch this report is about. A DAG produces one per branch.</summary>
	string? BranchAlias = null,
	/// <summary>The content-addressed checkpoint this run materialised, when --checkpoint was set.</summary>
	string? CheckpointKey = null);

public static class SampleRunExtensions
{
	/// <summary>
	/// Presents a <see cref="SampleRun"/> as the row-major traces the renderer navigates: trace
	/// <c>j</c> is row <c>j</c> of every stage, side by side.
	///
	/// The correspondence is exact only while the pipeline is 1:1. A stage that expands or
	/// aggregates has a different row count from its neighbour, and there is then no single
	/// "row j" running through the whole chain — a shorter stage simply has no cell in that
	/// column. That is a fact about the pipeline, not a defect of the view; the renderer reads
	/// each stage's <see cref="StageCapture.TotalSeen"/> to say where the cardinality changed
	/// rather than implying a correspondence that does not exist.
	/// </summary>
	public static List<SampleTrace> ToTraces(this SampleRun run)
	{
		var traces = new List<SampleTrace>();
		if (run.Stages.Count == 0) return traces;

		var depth = run.Stages.Max(s => s.Rows.Count);
		for (var j = 0; j < depth; j++)
		{
			var stages = new List<StageTrace>(run.Stages.Count);
			foreach (var stage in run.Stages)
				stages.Add(new StageTrace(stage.Schema, j < stage.Rows.Count ? stage.Rows[j] : null));

			traces.Add(new SampleTrace(stages));
		}

		return traces;
	}

	/// <summary>The rows leaving the last stage — what the pipeline would have written.</summary>
	public static IReadOnlyList<object?[]> FinalRows(this SampleRun run)
		=> run.Stages.Count == 0 ? Array.Empty<object?[]>() : run.Stages[^1].Rows;

	/// <summary>The schema leaving the last stage.</summary>
	public static IReadOnlyList<PipeColumnInfo> FinalSchema(this SampleRun run)
		=> run.Stages.Count == 0 ? Array.Empty<PipeColumnInfo>() : run.Stages[^1].Schema;
}
