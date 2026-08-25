using DtPipe.Core.Abstractions;
using DtPipe.Core.Options;

using DtPipe.Core.Pipelines;

using DtPipe.Transformers.Abstract;

namespace DtPipe.Transformers.Arrow.Mask;

public class MaskDataTransformerFactory : TransformerFactoryBase<MaskOptions>
{
	public MaskDataTransformerFactory(OptionsRegistry registry) : base(registry) { }

	public override string ComponentName => "mask";


	public override string Category => "Transformers";
	public override Type OptionsType => typeof(DtPipe.Transformers.Arrow.Mask.MaskOptions);

	protected override IDataTransformer? CreateFromTypedOptions(MaskOptions options)
	{
		return new MaskDataTransformer(options);
	}

	public override IDataTransformer CreateFromConfiguration(IEnumerable<(string Option, string Value)> configuration)
	{
		// Get config options (like SkipNull) from registry-bound options
		var registryOptions = Registry.Get<MaskOptions>();

		var options = new DtPipe.Transformers.Arrow.Mask.MaskOptions
		{
			Mask = [.. configuration.Select(x => x.Value)],
			SkipNull = registryOptions.SkipNull
		};
		return new MaskDataTransformer(options);
	}

	public override IDataTransformer? CreateFromYamlConfig(TransformerConfig config)
	{
		// For mask transformer, Mappings are key=column, value=pattern
		if (config.Mappings == null || config.Mappings.Count == 0)
			return null;

		// For mask transformer, Mappings are key=column, value=pattern
		// If value is empty, use key only (implies default mask)
		var mappings = config.Mappings.Select(kvp => string.IsNullOrEmpty(kvp.Value) ? kvp.Key : $"{kvp.Key}:{kvp.Value}").ToList();

		var skipNull = false;
		if (config.Options != null && config.Options.TryGetValue("skip-null", out var snStr))
		{
			bool.TryParse(snStr, out skipNull);
		}

		var options = new MaskOptions { Mask = mappings, SkipNull = skipNull };
		return new MaskDataTransformer(options);
	}
}
