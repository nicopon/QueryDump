using DtPipe.Core.Abstractions;
using DtPipe.Core.Options;

using DtPipe.Core.Pipelines;

using DtPipe.Transformers.Abstract;

namespace DtPipe.Transformers.Arrow.Null;

public class NullDataTransformerFactory : TransformerFactoryBase<NullOptions>
{
	public override string ComponentName => "null";

	public NullDataTransformerFactory(OptionsRegistry registry) : base(registry) { }



	public override string Category => "Transformers";

	protected override IDataTransformer? CreateFromTypedOptions(NullOptions options)
	{
		return new NullDataTransformer(options);
	}

	public override IDataTransformer CreateFromConfiguration(IEnumerable<(string Option, string Value)> configuration)
	{
		var options = new DtPipe.Transformers.Arrow.Null.NullOptions
		{
			Columns = [.. configuration.Select(x => x.Value)]
		};
		return new NullDataTransformer(options);
	}

	public override IDataTransformer? CreateFromYamlConfig(TransformerConfig config)
	{
		// For null transformer, Mappings keys are the column names (values are ignored)
		if (config.Mappings == null || config.Mappings.Count == 0)
			return null;

		var options = new DtPipe.Transformers.Arrow.Null.NullOptions { Columns = [.. config.Mappings.Keys] };
		return new NullDataTransformer(options);
	}
}
