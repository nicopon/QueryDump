using System.ComponentModel;
using DtPipe.Core.Attributes;
using DtPipe.Core.Options;

namespace DtPipe.Transformers.Row.Expand;

[Description("Expands a single row into multiple rows using a JavaScript expression that returns an array of row objects.")]
[ComponentHelp(
	usageNotes: "In YAML, place the JavaScript expression as a 'mappings' key with an empty value, same convention as 'filter'. The expression must return an array of row-shaped objects; each element becomes one output row.",
	examples: new[] {
		"transformers:\n  - type: expand\n    mappings:\n      \"row.tags.split(',').map(t => ({ ...row, tag: t.trim() }))\": \"\""
	})]
public class ExpandOptions : ITransformerOptions
{
	public static string Prefix => "expand";
	public static string DisplayName => "Expand Options";

	[ComponentOption("--expand", Description = "A JavaScript expression that returns an array of rows. Each element becomes a new row.")]
	public string[]? Expand { get; set; }

	public Dictionary<string, string> ExpandTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
