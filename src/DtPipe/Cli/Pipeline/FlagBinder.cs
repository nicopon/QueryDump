using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;

namespace DtPipe.Cli.Pipeline;

public static class FlagBinder
{
    public static void Bind(object target, string[] args, FlagRegistry registry, string prefix = "", bool strict = false)
    {
        var type = target.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith('-')) continue;

            var def = registry.Lookup(token);
            if (def == null)
            {
                if (strict)
                    throw new InvalidOperationException(
                        $"Unrecognized flag '{token}' for component '{(string.IsNullOrEmpty(prefix) ? type.Name : prefix)}'. " +
                        "Check the provider prefix and flag spelling (see 'dtpipe --help'), or remove --strict-bindings to skip unknown flags.");
                continue;
            }

            string? value = null;
            if (def.Arity != FlagArity.Boolean)
            {
                if (i + 1 < args.Length && !args[i+1].StartsWith('-'))
                {
                    value = args[++i];
                }
            }
            else
            {
                value = "true";
            }

            if (value == null) continue;

            bool matched = false;
            foreach (var prop in props)
            {
                if (Match(prop, def.Name, def.Aliases, prefix))
                {
                    matched = true;
                    SetValue(target, prop, value, strict);
                }
            }

            if (strict && !matched)
                throw new InvalidOperationException(
                    $"Flag '{token}' could not be bound to any property of '{type.Name}' for component '{prefix}'. " +
                    "The flag exists in the registry but does not map to this options type.");
        }
    }

    private static bool Match(PropertyInfo prop, string flagName, string[] aliases, string prefix)
    {
        var kebab = prop.Name.ToKebabCase();
        var names = new List<string> { $"--{kebab}" };
        if (!string.IsNullOrEmpty(prefix))
        {
            names.Add($"--{prefix.ToLowerInvariant()}-{kebab}");
            if (kebab == prefix.ToLowerInvariant()) names.Add($"--{prefix.ToLowerInvariant()}");
        }

        return names.Any(n => n.Equals(flagName, StringComparison.OrdinalIgnoreCase)) ||
               aliases.Any(a => names.Any(n => n.Equals(a, StringComparison.OrdinalIgnoreCase)));
    }

    private static void SetValue(object target, PropertyInfo prop, string value, bool strict = false)
    {
        try
        {
            var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (type == typeof(string)) prop.SetValue(target, value);
            else if (type == typeof(bool)) prop.SetValue(target, bool.Parse(value));
            else if (type == typeof(int)) prop.SetValue(target, int.Parse(value));
            else if (type == typeof(double)) prop.SetValue(target, double.Parse(value));
            else if (type.IsEnum) prop.SetValue(target, Enum.Parse(type, value, true));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            if (strict)
                throw new InvalidOperationException(
                    $"Failed to bind value '{value}' to option '{prop.Name}': {ex.Message}", ex);
            Console.Error.WriteLine($"Warning: FlagBinder could not bind '{prop.Name}': {ex.Message}");
        }
    }
}

public static class StringExtensions
{
    public static string ToKebabCase(this string str)
    {
        return string.Concat(str.Select((x, i) => i > 0 && char.IsUpper(x) ? "-" + x.ToString() : x.ToString())).ToLower();
    }
}
