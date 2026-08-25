using System.Collections.Concurrent;

namespace DtPipe.Core.Options;

/// <summary>
/// F14 — explicit scoped, thread-safe options container. Unlike <see cref="OptionsRegistry"/>
/// (AsyncLocal fork-on-scope), isolation is achieved by constructing one container per
/// scope and handing it explicitly to the code that needs it — no ambient state.
/// </summary>
public sealed class ScopedOptionsContainer : IOptionsProvider
{
    private readonly ConcurrentDictionary<Type, object> _options = new();

    /// <summary>Registers (or replaces) the instance for T.</summary>
    public T Register<T>(T options) where T : class, IOptionSet
    {
        _options[typeof(T)] = options;
        return options;
    }

    public void RegisterByType(Type optionType, object options)
        => _options[optionType] = options;

    /// <inheritdoc />
    public bool TryGet<T>(out T value) where T : class, IOptionSet, new()
    {
        if (_options.TryGetValue(typeof(T), out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = new T();
        return false;
    }

    /// <inheritdoc />
    public T Require<T>() where T : class, IOptionSet, new()
    {
        if (_options.TryGetValue(typeof(T), out var raw) && raw is T typed)
            return typed;

        throw new InvalidOperationException(
            $"Options of type '{typeof(T).Name}' were required but never bound in this scope. " +
            "Ensure the component's flags were provided and registered before use.");
    }

    public bool Has<T>() where T : class, IOptionSet => _options.ContainsKey(typeof(T));
}
