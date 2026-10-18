using System;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Core;

public sealed class DialogCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> ShowAsync<T>(Func<Task<T>> showDialog, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await showDialog();
        }
        finally
        {
            _gate.Release();
        }
    }
}
