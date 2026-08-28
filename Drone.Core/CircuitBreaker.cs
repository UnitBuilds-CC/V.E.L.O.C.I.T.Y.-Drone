using System;
using System.Threading;

namespace Drone.Core;

/// <summary>
/// Thread-safe circuit breaker with three states: Closed, Open, HalfOpen.
/// Protects against cascading failures by short-circuiting calls when the
/// downstream service is unhealthy, then periodically probing for recovery.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly object _lock = new();
    private readonly int _failureThreshold;
    private readonly TimeSpan _openTimeout;
    private int _consecutiveFailures;
    private CircuitState _state = CircuitState.Closed;
    private DateTime _openedAt;

    public CircuitBreaker(int failureThreshold = 5, TimeSpan? openTimeout = null)
    {
        _failureThreshold = failureThreshold;
        _openTimeout = openTimeout ?? TimeSpan.FromSeconds(30);
    }

    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                if (_state == CircuitState.Open && DateTime.UtcNow - _openedAt >= _openTimeout)
                    _state = CircuitState.HalfOpen;
                return _state;
            }
        }
    }

    public bool IsOpen => State == CircuitState.Open;

    /// <summary>
    /// Executes the given action within the circuit breaker.
    /// Throws <see cref="CircuitBrokenException"/> if the circuit is open.
    /// </summary>
    public void Execute(Action action)
    {
        var state = State;
        if (state == CircuitState.Open)
            throw new CircuitBrokenException("Circuit breaker is open — call rejected");

        try
        {
            action();
            OnSuccess();
        }
        catch (CircuitBrokenException) { throw; }
        catch
        {
            OnFailure();
            throw;
        }
    }

    /// <summary>
    /// Executes the given async function within the circuit breaker.
    /// Throws <see cref="CircuitBrokenException"/> if the circuit is open.
    /// </summary>
    public async System.Threading.Tasks.Task<T> ExecuteAsync<T>(Func<System.Threading.Tasks.Task<T>> func, CancellationToken ct = default)
    {
        var state = State;
        if (state == CircuitState.Open)
            throw new CircuitBrokenException("Circuit breaker is open — call rejected");

        try
        {
            var result = await func().ConfigureAwait(false);
            OnSuccess();
            return result;
        }
        catch (CircuitBrokenException) { throw; }
        catch
        {
            OnFailure();
            throw;
        }
    }

    /// <summary>
    /// Executes the given async action within the circuit breaker.
    /// Throws <see cref="CircuitBrokenException"/> if the circuit is open.
    /// </summary>
    public async System.Threading.Tasks.Task ExecuteAsync(Func<System.Threading.Tasks.Task> func, CancellationToken ct = default)
    {
        var state = State;
        if (state == CircuitState.Open)
            throw new CircuitBrokenException("Circuit breaker is open — call rejected");

        try
        {
            await func().ConfigureAwait(false);
            OnSuccess();
        }
        catch (CircuitBrokenException) { throw; }
        catch
        {
            OnFailure();
            throw;
        }
    }

    /// <summary>Manually reset the circuit breaker to closed state.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
        }
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
        }
    }

    private void OnFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_state == CircuitState.HalfOpen || _consecutiveFailures >= _failureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
            }
        }
    }
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>Thrown when a call is rejected because the circuit breaker is open.</summary>
public class CircuitBrokenException : Exception
{
    public CircuitBrokenException(string message) : base(message) { }
}
