using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dynatrace.OpenFeature.Tests;

/// <summary>
/// A count-down latch for asynchronous tests: signalled from callbacks, awaited by the test with a timeout.
/// </summary>
internal sealed class Latch
{
    private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _remaining;

    public Latch(int count = 1)
    {
        _remaining = count;
    }

    public void CountDown()
    {
        if (Interlocked.Decrement(ref _remaining) <= 0)
        {
            _completion.TrySetResult(true);
        }
    }

    /// <summary>Returns <c>true</c> if the latch reached zero within the timeout.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout)
    {
        var finished = await Task.WhenAny(_completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return finished == _completion.Task;
    }
}
