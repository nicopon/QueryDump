using DtPipe.Core.Abstractions;
using DtPipe.Core.Options;
using DtPipe.Core.Pipelines;

using DtPipe.Transformers.Abstract;

namespace DtPipe.Transformers.Arrow.Fake;

public class FakeDataTransformerFactory : TransformerFactoryBase<FakeOptions>
{

	public override string ComponentName => "fake";

	public FakeDataTransformerFactory(OptionsRegistry registry) : base(registry) { }



	public override string Category => "Transformers";

	public override IDataTransformer CreateFromConfiguration(IEnumerable<(string Option, string Value)> configuration)
	{
		var globalOptions = Registry.Get<DtPipe.Transformers.Arrow.Fake.FakeOptions>();
		var mappings = new List<string>();
		var locale = globalOptions.Locale;
		var seedColumns = new List<string>(globalOptions.SeedColumn);
		var seedRow = globalOptions.SeedRow;
		var seed = globalOptions.Seed;
		var skipNull = globalOptions.SkipNull;

		foreach (var (option, value) in configuration)
		{
			var opt = option.ToLowerInvariant();
			if (opt == "fake" || opt == "--fake") mappings.Add(value);
			else if (opt == "fake-locale" || opt == "--fake-locale" || opt == "locale") locale = value;
			else if (opt == "fake-seed-column" || opt == "--fake-seed-column" || opt == "seed-column")
			{
				if (!string.IsNullOrEmpty(value))
				{
					// Support comma-separated columns for composite seeds
					seedColumns.AddRange(value.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)));
				}
			}
			else if (opt == "fake-seed" || opt == "--fake-seed" || opt == "seed") { if (int.TryParse(value, out var sVal)) seed = sVal; }
			else if (opt == "fake-deterministic" || opt == "--fake-deterministic" || opt == "deterministic")
			{
				// Throw explicit exception for the deprecated option to guide users
				throw new ArgumentException("The option '--fake-deterministic' has been renamed to '--fake-seed-row'. Please update your scripts.");
			}
			else if (opt == "fake-seed-row" || opt == "--fake-seed-row" || opt == "seed-row") { if (bool.TryParse(value, out var srVal)) seedRow = srVal; }
			else if (opt == "fake-skip-null" || opt == "--fake-skip-null" || opt == "skip-null") { if (bool.TryParse(value, out var snVal)) skipNull = snVal; }
		}

		var options = new DtPipe.Transformers.Arrow.Fake.FakeOptions
		{
			Fake = mappings,
			Locale = locale,
			Seed = seed,
			SeedColumn = seedColumns,
			SeedRow = seedRow,
			SkipNull = skipNull
		};

		return new FakeDataTransformer(options);
	}

	protected override IDataTransformer? CreateFromTypedOptions(FakeOptions options)
	{
		return new FakeDataTransformer(options);
	}

	public override IDataTransformer? CreateFromYamlConfig(TransformerConfig config)
	{
		var mappings = new List<string>();
		if (config.Mappings != null)
		{
			foreach (var kvp in config.Mappings)
			{
				mappings.Add($"{kvp.Key}:{kvp.Value}");
			}
		}

		var options = new DtPipe.Transformers.Arrow.Fake.FakeOptions { Fake = mappings };

		if (config.Options != null)
		{
			if (config.Options.TryGetValue("locale", out var loc)) options = options with { Locale = loc };
			if (config.Options.TryGetValue("seed", out var s) && int.TryParse(s, out var sv)) options = options with { Seed = sv };
			if (config.Options.TryGetValue("seed-column", out var sc))
			{
				// Support comma-separated seed columns from YAML as well
				var cols = sc.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
				options = options with { SeedColumn = cols };
			}
			if (config.Options.TryGetValue("deterministic", out var d))
			{
				// Throw explicit exception for the deprecated YAML option
				throw new ArgumentException("The YAML option 'deterministic' has been renamed to 'seed-row'. Please update your configuration.");
			}
			if (config.Options.TryGetValue("seed-row", out var sr) && bool.TryParse(sr, out var srv)) options = options with { SeedRow = srv };
			if (config.Options.TryGetValue("skip-null", out var sn) && bool.TryParse(sn, out var snv)) options = options with { SkipNull = snv };
		}
		return new FakeDataTransformer(options);
	}
}
