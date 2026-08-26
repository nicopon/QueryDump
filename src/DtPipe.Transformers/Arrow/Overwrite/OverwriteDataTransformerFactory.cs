using DtPipe.Core.Abstractions;
using DtPipe.Core.Options;
using DtPipe.Core.Pipelines;

using DtPipe.Transformers.Abstract;

namespace DtPipe.Transformers.Arrow.Overwrite;

public class OverwriteDataTransformerFactory : TransformerFactoryBase<OverwriteOptions>
{

	public override string ComponentName => "overwrite";

	public OverwriteDataTransformerFactory(OptionsRegistry registry) : base(registry) { }



	public override string Category => "Transformers";


	protected override IDataTransformer? CreateFromTypedOptions(OverwriteOptions options)
	{
		return new OverwriteDataTransformer(options);
	}

	public override IDataTransformer CreateFromConfiguration(IEnumerable<(string Option, string Value)> configuration)
	{
		// Get config options (like SkipNull) from registry-bound options
		var registryOptions = Registry.Get<OverwriteOptions>();

		var options = new DtPipe.Transformers.Arrow.Overwrite.OverwriteOptions
		{
			Overwrite = configuration.Select(x => x.Value),
			SkipNull = registryOptions.SkipNull
		};
		return new OverwriteDataTransformer(options);
	}

	public override IDataTransformer? CreateFromYamlConfig(TransformerConfig config)
	{
		if (config.Mappings == null || config.Mappings.Count == 0)
			return null;

		// Convert YAML dict to "COLUMN:value" or "COLUMN=value" format
		// If value is empty, just return key (which might already contain the separator like "Col=Val")
		var mappings = config.Mappings.Select(kvp => string.IsNullOrEmpty(kvp.Value) ? kvp.Key : $"{kvp.Key}:{kvp.Value}");

		var skipNull = false;
		if (config.Options != null && config.Options.TryGetValue("skip-null", out var snStr))
		{
			bool.TryParse(snStr, out skipNull);
		}

		var options = new DtPipe.Transformers.Arrow.Overwrite.OverwriteOptions { Overwrite = mappings, SkipNull = skipNull };
		return new OverwriteDataTransformer(options);
	}
}
