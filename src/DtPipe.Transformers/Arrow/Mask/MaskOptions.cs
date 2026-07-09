using DtPipe.Core.Attributes;
using DtPipe.Core.Options;

namespace DtPipe.Transformers.Arrow.Mask;

public class MaskOptions : ITransformerOptions
{
	public static string Prefix => "mask";
	public static string DisplayName => "Mask Transformer";

	[ComponentOption(Description = "Mask column. Format: COLUMN:pattern (# = keep, other = replace) or simply COLUMN for default full mask (15 asterisks).")]
	public IEnumerable<string> Mask { get; set; } = [];

	[ComponentOption(Description = "Skip mask when source value is null")]
	public bool SkipNull { get; set; } = false;
}
