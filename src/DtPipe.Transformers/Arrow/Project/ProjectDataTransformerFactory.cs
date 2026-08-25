using DtPipe.Core.Abstractions;
using DtPipe.Core.Options;
using DtPipe.Core.Pipelines;

using DtPipe.Transformers.Abstract;

namespace DtPipe.Transformers.Arrow.Project;

public class ProjectDataTransformerFactory : TransformerFactoryBase<ProjectOptions>
{
	public ProjectDataTransformerFactory(OptionsRegistry registry) : base(registry) { }

	public override string ComponentName => "project";


	public override string Category => "Transformers";
	public override Type OptionsType => typeof(DtPipe.Transformers.Arrow.Project.ProjectOptions);

	protected override IDataTransformer? CreateFromTypedOptions(ProjectOptions options)
	{
		return new ProjectDataTransformer(options);
	}

	public override IDataTransformer CreateFromConfiguration(IEnumerable<(string Option, string Value)> configuration)
	{
		var options = new ProjectOptions();
        var projects = new List<string>();
        var drops = new List<string>();
        var renames = new List<string>();

        foreach (var (opt, val) in configuration)
        {
            if (opt.Equals("project", StringComparison.OrdinalIgnoreCase) || opt.Equals("--project", StringComparison.OrdinalIgnoreCase))
                projects.Add(val);
            else if (opt.Equals("drop", StringComparison.OrdinalIgnoreCase) || opt.Equals("--drop", StringComparison.OrdinalIgnoreCase))
                drops.Add(val);
            else if (opt.Equals("rename", StringComparison.OrdinalIgnoreCase) || opt.Equals("--rename", StringComparison.OrdinalIgnoreCase))
                renames.Add(val);
        }

        options.Project = projects;
        options.Drop = drops;
        options.Rename = renames;

		return new ProjectDataTransformer(options);
	}

	public override IDataTransformer? CreateFromYamlConfig(TransformerConfig config)
	{
		var options = new ProjectOptions();

		// Handle "project" (whitelist)
		if (config.Mappings != null && config.Mappings.Count > 0)
		{
			options.Project = config.Mappings.Keys;
		}
		else if (config.Options != null && config.Options.TryGetValue("project", out var projectVal))
		{
			options.Project = new[] { projectVal };
		}

		// Handle "drop" (blacklist)
		if (config.Options != null && config.Options.TryGetValue("drop", out var dropVal))
		{
			options.Drop = new[] { dropVal };
		}

        // Handle "rename"
        if (config.Options != null && config.Options.TryGetValue("rename", out var renameVal))
        {
            options.Rename = new[] { renameVal };
        }

		if (!options.Project.Any() && !options.Drop.Any() && !options.Rename.Any())
			return null;

		return new ProjectDataTransformer(options);
	}
}
