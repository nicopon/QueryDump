namespace DtPipe.Core.Options;

/// <summary>
/// F14 — typed access to bound option instances without ambient fallbacks.
/// Implementations provide TryGet/Require semantics; missing required options surface
/// as exceptions instead of silent defaults.
/// </summary>
public interface IOptionsProvider
{
    /// <summary>Attempts to retrieve registered options of type T.</summary>
    bool TryGet<T>(out T value) where T : class, IOptionSet, new();

    /// <summary>Returns registered options of type T, throwing when never bound.</summary>
    T Require<T>() where T : class, IOptionSet, new();
}
