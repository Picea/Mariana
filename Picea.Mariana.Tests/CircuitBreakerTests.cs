using Picea.Mariana.CircuitBreaker;

namespace Picea.Mariana.Tests;

public class CircuitBreakerTests
{
    // =========================================================================
    // Execute — success
    // =========================================================================

    [Fact]
    public async Task Execute_succeeds_when_circuit_closed()
    {
        using var breaker = new CircuitBreaker.CircuitBreaker(new CircuitBreakerOptions(FailureThreshold: 3));

        var result = await breaker.Execute(_ => new ValueTask<int>(42));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task Execute_resets_failure_count_on_success()
    {
        using var breaker = new CircuitBreaker.CircuitBreaker(new CircuitBreakerOptions(FailureThreshold: 3));

        // Two failures
        await breaker.Execute<int>(_ => throw new Exception("fail 1"));
        await breaker.Execute<int>(_ => throw new Exception("fail 2"));

        // One success resets the counter
        await breaker.Execute(_ => new ValueTask<int>(1));

        // Two more failures should NOT trip (counter was reset)
        await breaker.Execute<int>(_ => throw new Exception("fail 3"));
        await breaker.Execute<int>(_ => throw new Exception("fail 4"));

        // Should still be closed (only 2 consecutive failures)
        var result = await breaker.Execute(_ => new ValueTask<int>(99));
        Assert.True(result.IsOk);
    }

    // =========================================================================
    // Execute — circuit opens after threshold
    // =========================================================================

    [Fact]
    public async Task Execute_trips_after_failure_threshold()
    {
        using var breaker = new CircuitBreaker.CircuitBreaker(
            new CircuitBreakerOptions(FailureThreshold: 3, BreakDuration: TimeSpan.FromSeconds(60)));

        // Trip the circuit with 3 consecutive failures
        for (var i = 0; i < 3; i++)
            await breaker.Execute<int>(_ => throw new Exception($"fail {i + 1}"));

        // Next call should be rejected
        var result = await breaker.Execute(_ => new ValueTask<int>(42));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.CircuitOpen, result.Error.Reason);
        Assert.Equal("CircuitBreaker", result.Error.Strategy);
    }

    // =========================================================================
    // Execute — half-open probe
    // =========================================================================

    [Fact]
    public async Task Execute_allows_probe_after_break_duration()
    {
        using var breaker = new CircuitBreaker.CircuitBreaker(
            new CircuitBreakerOptions(FailureThreshold: 2, BreakDuration: TimeSpan.FromMilliseconds(50)));

        // Trip the circuit
        await breaker.Execute<int>(_ => throw new Exception("fail 1"));
        await breaker.Execute<int>(_ => throw new Exception("fail 2"));

        // Wait for break duration
        await Task.Delay(100);

        // Probe should succeed and reset circuit
        var result = await breaker.Execute(_ => new ValueTask<string>("probe ok"));

        Assert.True(result.IsOk);
        Assert.Equal("probe ok", result.Value);

        // Circuit should be closed now
        Assert.IsType<CircuitBreakerState.Closed>(breaker.State);
    }

    [Fact]
    public async Task Execute_probe_failure_reopens_circuit()
    {
        using var breaker = new CircuitBreaker.CircuitBreaker(
            new CircuitBreakerOptions(FailureThreshold: 2, BreakDuration: TimeSpan.FromMilliseconds(50)));

        // Trip the circuit
        await breaker.Execute<int>(_ => throw new Exception("fail 1"));
        await breaker.Execute<int>(_ => throw new Exception("fail 2"));

        // Wait for break duration
        await Task.Delay(100);

        // Probe fails — should re-open
        await breaker.Execute<int>(_ => throw new Exception("probe fail"));

        // Circuit should be open again
        Assert.IsType<CircuitBreakerState.Open>(breaker.State);

        // Next call should be rejected
        var result = await breaker.Execute(_ => new ValueTask<int>(42));
        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.CircuitOpen, result.Error.Reason);
    }

    // =========================================================================
    // Manual reset
    // =========================================================================

    [Fact]
    public async Task Reset_closes_circuit()
    {
        using var breaker = new CircuitBreaker.CircuitBreaker(
            new CircuitBreakerOptions(FailureThreshold: 2, BreakDuration: TimeSpan.FromSeconds(60)));

        // Trip the circuit
        await breaker.Execute<int>(_ => throw new Exception("fail 1"));
        await breaker.Execute<int>(_ => throw new Exception("fail 2"));

        // Circuit is open
        Assert.IsType<CircuitBreakerState.Open>(breaker.State);

        // Manual reset
        await breaker.Reset();

        // Circuit should be closed
        Assert.IsType<CircuitBreakerState.Closed>(breaker.State);

        var result = await breaker.Execute(_ => new ValueTask<int>(42));
        Assert.True(result.IsOk);
    }

    // =========================================================================
    // ShouldHandle filter
    // =========================================================================

    [Fact]
    public async Task Execute_ignores_unhandled_exceptions_for_counting()
    {
        using var breaker = new CircuitBreaker.CircuitBreaker(
            new CircuitBreakerOptions(
                FailureThreshold: 2,
                ShouldHandle: ex => ex is TimeoutException));

        // These don't count toward the threshold
        await breaker.Execute<int>(_ => throw new ArgumentException("not tracked"));
        await breaker.Execute<int>(_ => throw new ArgumentException("not tracked"));
        await breaker.Execute<int>(_ => throw new ArgumentException("not tracked"));

        // Circuit should still be closed
        var result = await breaker.Execute(_ => new ValueTask<int>(42));
        Assert.True(result.IsOk);
    }

    [Fact]
    public async Task Execute_counts_handled_exceptions()
    {
        using var breaker = new CircuitBreaker.CircuitBreaker(
            new CircuitBreakerOptions(
                FailureThreshold: 2,
                BreakDuration: TimeSpan.FromSeconds(60),
                ShouldHandle: ex => ex is TimeoutException));

        await breaker.Execute<int>(_ => throw new TimeoutException("tracked 1"));
        await breaker.Execute<int>(_ => throw new TimeoutException("tracked 2"));

        // Circuit should be open now
        var result = await breaker.Execute(_ => new ValueTask<int>(42));
        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.CircuitOpen, result.Error.Reason);
    }

    // =========================================================================
    // Default options
    // =========================================================================

    [Fact]
    public void Default_failure_threshold_is_5()
    {
        var opts = new CircuitBreakerOptions();
        Assert.Equal(5, opts.FailureThreshold);
    }

    [Fact]
    public void Default_break_duration_is_30_seconds()
    {
        var opts = new CircuitBreakerOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), opts.EffectiveBreakDuration);
    }
}
