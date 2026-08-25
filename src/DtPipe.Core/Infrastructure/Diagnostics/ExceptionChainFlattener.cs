using System.Text;

namespace DtPipe.Core.Infrastructure.Diagnostics;

/// <summary>
/// Flattens an exception chain (InnerException and AggregateException links) into a
/// stable multi-line "TypeName: Message" representation so that fault logs preserve
/// the full causal chain instead of only the outermost message.
/// </summary>
public static class ExceptionChainFlattener
{
    /// <summary>
    /// Formats the full exception chain, one line per link:
    /// <c>  -&gt; TypeName: Message</c>. AggregateException wrappers with a single
    /// inner exception are transparent (the wrapper itself is not emitted).
    /// </summary>
    public static string Format(Exception? exception)
    {
        if (exception is null) return string.Empty;

        var sb = new StringBuilder();
        var first = true;
        foreach (var link in EnumerateLinks(exception))
        {
            sb.Append(first ? "-> " : Environment.NewLine + "-> ");
            sb.Append(link.GetType().Name).Append(": ").Append(link.Message);
            first = false;
        }
        return sb.ToString();
    }

    private static IEnumerable<Exception> EnumerateLinks(Exception exception)
    {
        var current = exception;
        var visited = new HashSet<Exception>();
        while (current is not null && visited.Add(current))
        {
            // Skip transparent AggregateException wrappers (single inner or empty).
            if (current is AggregateException { InnerExceptions.Count: 1 } agg)
            {
                current = agg.InnerExceptions[0];
                continue;
            }
            if (current is AggregateException multi && multi.InnerExceptions.Count == 0)
            {
                yield return current;
                yield break;
            }
            yield return current;
            current = current.InnerException;
        }
    }
}
