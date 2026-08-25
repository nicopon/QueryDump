using System.Collections.Generic;
using System.Reflection;
using DtPipe.Core.Attributes;
using DtPipe.Core.Options;

namespace DtPipe.Cli.Pipeline;

/// <summary>
/// Sole owner of the property → flag-name derivation (F8). Flag-def generation, CLI
/// binding, YAML key matching and shell completion all resolve names through this class.
///
/// Precedence: <see cref="ICliOptionMetadata"/> PropertyToFlag mapping &gt;
/// <see cref="ComponentOptionAttribute"/>.Name &gt; derived kebab-case
/// (<c>--prop</c>, or <c>--prefix-prop</c> when the options type declares a Prefix;
/// a property named exactly like the prefix maps to bare <c>--prefix</c>).
/// </summary>
public static class FlagNameDeriver
{
    public static string GetPrefix(Type optionsType)
        => optionsType.GetProperty("Prefix", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
               ?.GetValue(null) as string ?? "";

    /// <summary>
    /// Derives the canonical flag name for a property, honoring the type's metadata map.
    /// </summary>
    public static string DeriveCanonical(PropertyInfo property, Type optionsType, IReadOnlyDictionary<string, string>? metadata)
    {
        string? metadataFlag = null;
        if (metadata != null && metadata.TryGetValue(property.Name, out var mappedFlag))
            metadataFlag = mappedFlag;

        var cliOptionAttr = property.GetCustomAttribute<ComponentOptionAttribute>();

        var flagName = metadataFlag ?? cliOptionAttr?.Name;
        if (!string.IsNullOrEmpty(flagName))
            return flagName!;

        var prefix = GetPrefix(optionsType);
        var kebab = property.Name.ToKebabCase();
        return kebab == prefix.ToLowerInvariant()
            ? $"--{prefix.ToLowerInvariant()}"
            : (string.IsNullOrEmpty(prefix) ? $"--{kebab}" : $"--{prefix.ToLowerInvariant()}-{kebab}");
    }
}

public static class StringExtensions
{
    public static string ToKebabCase(this string str)
    {
        return string.Concat(str.Select((x, i) => i > 0 && char.IsUpper(x) ? "-" + x.ToString() : x.ToString())).ToLower();
    }
}
