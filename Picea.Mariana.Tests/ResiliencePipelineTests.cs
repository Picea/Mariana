using Picea.Mariana.CircuitBreaker;
using Picea.Mariana.Pipeline;
using Picea.Mariana.RateLimiter;
using Picea.Mariana.Retry;
using Picea.Mariana.Timeout;

namespace Picea.Mariana.Tests;

public class ResiliencePipelineTests : IDisposable
{
    private readonly CircuitBreaker.CircuitBreaker _circuitBreaker = new(new CircuitBreakerOptions(
        FailureThreshold: 3,
        BreakDuration: TimeSpan.FromMilliseconds(50)));

    private readonly RateLimiter.RateLimiter _rateLimiter = new(new RateLimiterOptions(
        PermitLimit: 10,
        Window: TimeSpan.FromHours(1)));

    public void Dispose()
    {
        _circuitBreaker.Dispose();
        _rateLimiter.Dispose();
    }

    [Fact]
    public async Task Identity_strategy_passes_through()
    {
        var result = await ResilienceStrategy.Identity<int>()
            .Execute(_ => ValueTask.FromResult(42));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task Identity_strategy_propagates_operation_failure()
    {
        var result = await ResilienceStrategy.Identity<int>()
            .Execute(_ => throw new InvalidOperationException("boom"));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Unknown, result.Error.Reason);
        Assert.Contains("boom", result.Error.Message);
    }

    [Fact]
    public async Task Single_retry_strategy_recovers_transient_failure()
    {
        var callCount = 0;

        var result = await ResilienceStrategy
            .WithRetry<string>(new RetryOptions(MaxAttempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1)))
            .Execute(async ct =>
            {
                callCount++;
                if (callCount < 3)
                    throw new InvalidOperationException("transient");
                return "recovered";
            });

        Assert.True(result.IsOk);
        Assert.Equal("recovered", result.Value);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task Single_timeout_strategy_enforces_deadline()
    {
        var result = await ResilienceStrategy
            .WithTimeout<int>(new TimeoutOptions(TimeSpan.FromMilliseconds(50)))
            .Execute(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return 42;
            });

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Timeout, result.Error.Reason);
    }

    [Fact]
    public async Task Retry_wraps_timeout_so_timeout_triggers_retry()
    {
        var callCount = 0;

        var pipeline = ResilienceStrategy
            .WithRetry<string>(new RetryOptions(MaxAttempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1)))
            .Then(ResilienceStrategy.WithTimeout<string>(new TimeoutOptions(TimeSpan.FromMilliseconds(50))));

        var result = await pipeline.Execute(async ct =>
        {
            callCount++;
            if (callCount < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return "should not reach";
            }
            return "success";
        });

        Assert.True(result.IsOk);
        Assert.Equal("success", result.Value);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task Circuit_breaker_strategy_rejects_when_open()
    {
        // Trip the circuit breaker
        for (var i = 0; i < 3; i++)
        {
            await _circuitBreaker.Execute<int>(
                _ => throw new InvalidOperationException("fail"));
        }

        var result = await ResilienceStrategy
            .WithCircuitBreaker<int>(_circuitBreaker)
            .Execute(_ => ValueTask.FromResult(42));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.CircuitOpen, result.Error.Reason);
    }

    [Fact]
    public async Task Rate_limiter_strategy_rejects_when_exhausted()
    {
        using var limiter = new RateLimiter.RateLimiter(new RateLimiterOptions(
            PermitLimit: 1,
            Window: TimeSpan.FromHours(1)));

        var strategy = ResilienceStrategy.WithRateLimiter<int>(limiter);

        var first = await strategy.Execute(_ => ValueTask.FromResult(1));
        Assert.True(first.IsOk);

        var second = await strategy.Execute(_ => ValueTask.FromResult(2));
        Assert.True(second.IsErr);
        Assert.Equal(FailureReason.RateLimited, second.Error.Reason);
    }

    [Fact]
    public async Task Fallback_strategy_provides_alternative_on_failure()
    {
        var result = await ResilienceStrategy
            .WithFallback("default")
            .Execute(_ => throw new InvalidOperationException("fail"));

        Assert.True(result.IsOk);
        Assert.Equal("default", result.Value);
    }

    [Fact]
    public async Task Fallback_strategy_with_async_fallback()
    {
        var result = await ResilienceStrategy
            .WithFallback(_ => ValueTask.FromResult("fallback value"))
            .Execute(_ => throw new InvalidOperationException("fail"));

        Assert.True(result.IsOk);
        Assert.Equal("fallback value", result.Value);
    }

    [Fact]
    public async Task Full_pipeline_retry_timeout_circuit_breaker()
    {
        var callCount = 0;

        var pipeline = ResilienceStrategy
            .WithRetry<string>(new RetryOptions(MaxAttempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1)))
            .Then(ResilienceStrategy.WithTimeout<string>(new TimeoutOptions(TimeSpan.FromSeconds(5))))
            .Then(ResilienceStrategy.WithCircuitBreaker<string>(_circuitBreaker));

        var result = await pipeline.Execute(async ct =>
        {
            callCount++;
            if (callCount < 2)
                throw new InvalidOperationException("transient");
            return "success";
        });

        Assert.True(result.IsOk);
        Assert.Equal("success", result.Value);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Pipeline_returns_err_on_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await ResilienceStrategy
            .WithRetry<int>()
            .Execute(
                async ct =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    return 42;
                },
                cts.Token);

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Cancelled, result.Error.Reason);
    }

    [Fact]
    public async Task Custom_strategy_integrates_with_pipeline()
    {
        var intercepted = false;
        ResilienceStrategy<int> loggingStrategy = async (operation, ct) =>
        {
            intercepted = true;
            var value = await operation(ct).ConfigureAwait(false);
            return Result<int, ResilienceError>.Ok(value);
        };

        var result = await loggingStrategy
            .Execute(_ => ValueTask.FromResult(42));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
        Assert.True(intercepted);
    }

    [Fact]
    public async Task Pipeline_preserves_execution_order()
    {
        var order = new List<string>();

        ResilienceStrategy<int> first = async (operation, ct) =>
        {
            order.Add("first-before");
            var value = await operation(ct).ConfigureAwait(false);
            order.Add("first-after");
            return Result<int, ResilienceError>.Ok(value);
        };

        ResilienceStrategy<int> second = async (operation, ct) =>
        {
            order.Add("second-before");
            var value = await operation(ct).ConfigureAwait(false);
            order.Add("second-after");
            return Result<int, ResilienceError>.Ok(value);
        };

        var pipeline = first.Then(second);

        await pipeline.Execute(_ =>
        {
            order.Add("operation");
            return ValueTask.FromResult(42);
        });

        Assert.Equal(["first-before", "second-before", "operation", "second-after", "first-after"], order);
    }

    [Fact]
    public async Task Where_conditionally_applies_strategy()
    {
        var applied = false;

        ResilienceStrategy<int> tracked = async (operation, ct) =>
        {
            applied = true;
            var value = await operation(ct).ConfigureAwait(false);
            return Result<int, ResilienceError>.Ok(value);
        };

        // Predicate returns false — strategy should not be applied
        var guarded = tracked.Where(() => false);
        var result = await guarded.Execute(_ => ValueTask.FromResult(42));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
        Assert.False(applied);

        // Predicate returns true — strategy should be applied
        var enabled = tracked.Where(() => true);
        result = await enabled.Execute(_ => ValueTask.FromResult(42));

        Assert.True(result.IsOk);
        Assert.True(applied);
    }

    [Fact]
    public async Task Catch_recovers_from_strategy_error()
    {
        var pipeline = ResilienceStrategy
            .WithRetry<string>(new RetryOptions(MaxAttempts: 1, BaseDelay: TimeSpan.FromMilliseconds(1)))
            .Catch(error => error.Reason is FailureReason.RetriesExhausted
                ? Result<string, ResilienceError>.Ok("recovered")
                : Result<string, ResilienceError>.Err(error));

        var result = await pipeline
            .Execute(_ => throw new InvalidOperationException("always fails"));

        Assert.True(result.IsOk);
        Assert.Equal("recovered", result.Value);
    }
}
