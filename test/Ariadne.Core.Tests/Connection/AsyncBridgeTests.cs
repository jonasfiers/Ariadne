using System;
using System.Threading.Tasks;
using Ariadne.Core.Connection;
using Xunit;

namespace Ariadne.Core.Tests.Connection;

/// <summary>
/// Unit tests for <see cref="AsyncBridge"/> (spec §2): the sync-over-async block must surface the
/// <b>original</b> exception, not an <see cref="AggregateException"/> (which <c>.Result</c>/<c>.Wait()</c>
/// would produce). Getting this wrong breaks the Feature 09 exception-mapping table, so it is asserted
/// directly.
/// </summary>
public class AsyncBridgeTests
{
    private sealed class MarkerException : Exception
    {
        public MarkerException(string m) : base(m) { }
    }

    [Fact]
    public void RunSync_returns_the_value_of_a_completed_task()
    {
        int result = AsyncBridge.RunSync(Task.FromResult(42));
        Assert.Equal(42, result);
    }

    [Fact]
    public void RunSync_void_completes_a_completed_task()
    {
        // Should simply return without throwing.
        AsyncBridge.RunSync(Task.CompletedTask);
    }

    [Fact]
    public void RunSync_value_surfaces_original_exception_not_aggregate()
    {
        Task<int> faulted = Task.FromException<int>(new MarkerException("boom"));

        var ex = Assert.Throws<MarkerException>(() => AsyncBridge.RunSync(faulted));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void RunSync_void_surfaces_original_exception_not_aggregate()
    {
        Task faulted = Task.FromException(new MarkerException("bang"));

        var ex = Assert.Throws<MarkerException>(() => AsyncBridge.RunSync(faulted));
        Assert.Equal("bang", ex.Message);
    }

    [Fact]
    public void RunSync_surfaces_original_from_a_genuinely_async_fault()
    {
        // A task that actually yields before faulting (not a pre-completed FromException).
        async Task Faulting()
        {
            await Task.Yield();
            throw new MarkerException("async-boom");
        }

        var ex = Assert.Throws<MarkerException>(() => AsyncBridge.RunSync(Faulting()));
        Assert.Equal("async-boom", ex.Message);
    }

    [Fact]
    public void RunSync_actually_blocks_until_completion()
    {
        async Task<int> Delayed()
        {
            await Task.Delay(30).ConfigureAwait(false);
            return 7;
        }

        Assert.Equal(7, AsyncBridge.RunSync(Delayed()));
    }
}
