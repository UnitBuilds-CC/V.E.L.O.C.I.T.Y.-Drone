using Xunit;
using Drone.Core;

namespace Drone.Tests;

/// <summary>
/// Tests for the CircuitBreaker: state transitions, failure thresholds, and recovery.
/// </summary>
public class CircuitBreakerTests
{
    [Fact]
    public void CircuitBreaker_InitialState_IsClosed()
    {
        var cb = new CircuitBreaker();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    [Fact]
    public void CircuitBreaker_BelowThreshold_StaysClosed()
    {
        var cb = new CircuitBreaker(failureThreshold: 3);
        for (int i = 0; i < 2; i++)
        {
            try { cb.Execute(() => throw new InvalidOperationException("fail")); }
            catch { /* expected */ }
        }
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    [Fact]
    public void CircuitBreaker_AtThreshold_TransitionsToOpen()
    {
        var cb = new CircuitBreaker(failureThreshold: 3);
        for (int i = 0; i < 3; i++)
        {
            try { cb.Execute(() => throw new InvalidOperationException("fail")); }
            catch { /* expected */ }
        }
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.True(cb.IsOpen);
    }

    [Fact]
    public void CircuitBreaker_WhenOpen_RejectsCalls()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openTimeout: TimeSpan.FromMinutes(5));
        try { cb.Execute(() => throw new InvalidOperationException("fail")); }
        catch { /* expected — triggers open */ }

        Assert.True(cb.IsOpen);
        var ex = Assert.Throws<CircuitBrokenException>(() => cb.Execute(() => { }));
        Assert.Contains("open", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CircuitBreaker_AfterTimeout_TransitionsToHalfOpen()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openTimeout: TimeSpan.FromMilliseconds(50));
        try { cb.Execute(() => throw new InvalidOperationException("fail")); }
        catch { /* expected */ }

        Assert.True(cb.IsOpen);

        // Wait for the open timeout to expire
        Thread.Sleep(100);

        Assert.Equal(CircuitState.HalfOpen, cb.State);
        Assert.False(cb.IsOpen);
    }

    [Fact]
    public void CircuitBreaker_HalfOpen_Success_ClosesCircuit()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openTimeout: TimeSpan.FromMilliseconds(50));
        try { cb.Execute(() => throw new InvalidOperationException("fail")); }
        catch { /* expected */ }

        Thread.Sleep(100);
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // Successful call should close the circuit
        cb.Execute(() => { /* success */ });
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public void CircuitBreaker_HalfOpen_Failure_ReopensCircuit()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openTimeout: TimeSpan.FromMilliseconds(50));
        try { cb.Execute(() => throw new InvalidOperationException("fail")); }
        catch { /* expected */ }

        Thread.Sleep(100);
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        // Failed call in half-open should re-open
        try { cb.Execute(() => throw new InvalidOperationException("fail again")); }
        catch { /* expected */ }

        Assert.Equal(CircuitState.Open, cb.State);
        Assert.True(cb.IsOpen);
    }

    [Fact]
    public void CircuitBreaker_Success_ResetsFailureCount()
    {
        var cb = new CircuitBreaker(failureThreshold: 3);

        // 2 failures
        try { cb.Execute(() => throw new InvalidOperationException("fail")); } catch { }
        try { cb.Execute(() => throw new InvalidOperationException("fail")); } catch { }

        // 1 success resets counter
        cb.Execute(() => { });
        Assert.Equal(CircuitState.Closed, cb.State);

        // 2 more failures should NOT open (counter was reset)
        try { cb.Execute(() => throw new InvalidOperationException("fail")); } catch { }
        try { cb.Execute(() => throw new InvalidOperationException("fail")); } catch { }
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public void CircuitBreaker_Reset_ReturnsToClosed()
    {
        var cb = new CircuitBreaker(failureThreshold: 1);
        try { cb.Execute(() => throw new InvalidOperationException("fail")); }
        catch { /* expected */ }
        Assert.True(cb.IsOpen);

        cb.Reset();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    [Fact]
    public async Task CircuitBreaker_ExecuteAsync_WhenOpen_ThrowsCircuitBrokenException()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openTimeout: TimeSpan.FromMinutes(5));
        try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")); }
        catch { /* expected */ }

        Assert.True(cb.IsOpen);
        await Assert.ThrowsAsync<CircuitBrokenException>(async () =>
            await cb.ExecuteAsync<int>(() => Task.FromResult(42)));
    }

    [Fact]
    public async Task CircuitBreaker_ExecuteAsync_Success_ClosesCircuit()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openTimeout: TimeSpan.FromMilliseconds(50));
        try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")); }
        catch { /* expected */ }

        Thread.Sleep(100);
        var result = await cb.ExecuteAsync(() => Task.FromResult(42));
        Assert.Equal(42, result);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_ExecuteAsyncVoid_WhenOpen_ThrowsCircuitBrokenException()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, openTimeout: TimeSpan.FromMinutes(5));
        try { await cb.ExecuteAsync(() => throw new InvalidOperationException("fail")); }
        catch { /* expected */ }

        Assert.True(cb.IsOpen);
        await Assert.ThrowsAsync<CircuitBrokenException>(async () =>
            await cb.ExecuteAsync(() => Task.CompletedTask));
    }
}
