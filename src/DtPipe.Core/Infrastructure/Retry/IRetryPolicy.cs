using System;
using System.Threading;
using System.Threading.Tasks;

namespace DtPipe.Core.Infrastructure.Retry;

public interface IRetryPolicy
{
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct);
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
}
