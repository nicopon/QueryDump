using DtPipe.Core.Options;

namespace DtPipe.Cli.Infrastructure;

/// <summary>
/// Carries the resolved (selector-stripped) connection strings for the reader and writer of a branch.
/// Registered in the OptionsRegistry by LinearPipelineService so that CliStreamReaderFactory and
/// CliDataWriterFactory can resolve routing data without coupling PipelineOptions to adapter concerns.
/// </summary>
public class ConnectionRoute : IOptionSet
{
    public static string Prefix => "route";
    public static string DisplayName => "Connection Route";

    public string Input { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;

    /// <summary>
    /// The "+{variant}" qualifier stripped off the input selector ("mysql" for "duck+mysql:"),
    /// or null when the selector carried none. Held per-side because a single branch can read
    /// through one variant and write through another.
    /// </summary>
    public string? InputVariant { get; init; }

    /// <summary>The "+{variant}" qualifier stripped off the output selector, or null.</summary>
    public string? OutputVariant { get; init; }

    public ConnectionRoute() { }
    public ConnectionRoute(string input, string output)
    {
        Input = input;
        Output = output;
    }

    public ConnectionRoute(string input, string output, string? inputVariant, string? outputVariant)
    {
        Input = input;
        Output = output;
        InputVariant = inputVariant;
        OutputVariant = outputVariant;
    }
}
