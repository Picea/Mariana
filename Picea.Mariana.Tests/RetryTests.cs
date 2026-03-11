using Picea.Mariana.Retry;

namespace Picea.Mariana.Tests;

public class RetryTests
{
    // =========================================================================
    // Execute — success cases
    // =========================================================================

    [Fact]
    public async Task Execute_succeeds_on_first_attempt()
    {
        var result = await Retry.Retry.Execute(
            _ => new ValueTask<int>(42),
            new RetryOptions(MaxAttempts: 3));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task Execute_succeeds_after_transient_failure()
    {
        var attempt = 0;
        var result = await Retry.Retry.Execute(
            _ =>
            {
                attempt++;
                if (attempt < 3)
                    throw new InvalidOperationException($"Attempt {attempt} failed");
                return new ValueTask<string>("success");
            },
            new RetryOptions(MaxAttempts: 3, Backoff: BackoffType.Constant, BaseDelay: TimeSpan.FromMilliseconds(1)));

        Assert.True(result.IsOk);
        Assert.Equal("success", result.Value);
        Assert.Equal(3, attempt);
    }

    [Fact]
    public async Task Execute_succeeds_on_second_attempt()
    {
        var attempt = 0;
        var result = await Retry.Retry.Execute(
            _ =>
            {
                attempt++;
                if (attempt == 1)
                    throw new TimeoutException("first attempt timeout");
                return new ValueTask<int>(99);
            },
            new RetryOptions(MaxAttempts: 5, Backoff: BackoffType.Constant, BaseDelay: TimeSpan.FromMilliseconds(1)));

        Assert.True(result.IsOk);
        Assert.Equal(99, result.Value);
        Assert.Equal(2, attempt);
    }

    // =========================================================================
    // Execute — failure cases
    // =========================================================================

    [Fact]
    public async Task Execute_returns_error_when_all_attempts_exhausted()
    {
        var attempt = 0;
        var result = await Retry.Retry.Execute<int>(
            _ =>
            {
                attempt++;
                throw new InvalidOperationException($"Attempt {attempt}");
            },
            new RetryOptions(MaxAttempts: 3, Backoff: BackoffType.Constant, BaseDelay: TimeSpan.FromMilliseconds(1)));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.RetriesExhausted, result.Error.Reason);
        Assert.Equal("Retry", result.Error.Strategy);
        Assert.NotNull(result.Error.Exception);
        Assert.Equal(3, attempt);
    }

    [Fact]
    public async Task Execute_single_attempt_no_retries()
    {
        var result = await Retry.Retry.Execute<int>(
            _ => throw new Exception("fail"),
            new RetryOptions(MaxAttempts: 1));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.RetriesExhausted, result.Error.Reason);
    }

    // =========================================================================
    // Execute — ShouldRetry predicate
    // =========================================================================

    [Fact]
    public async Task Execute_stops_on_non_retryable_exception()
    {
        var attempt = 0;
        var result = await Retry.Retry.Execute<int>(
            _ =>
            {
                attempt++;
                throw new ArgumentException("bad input");
            },
            new RetryOptions(
                MaxAttempts: 5,
                Backoff: BackoffType.Constant,
                BaseDelay: TimeSpan.FromMilliseconds(1),
                ShouldRetry: ex => ex is not ArgumentException));

        Assert.True(result.IsErr);
        Assert.Equal(1, attempt);
        Assert.IsType<ArgumentException>(result.Error.Exception);
    }

    [Fact]
    public async Task Execute_retries_only_matching_exceptions()
    {
        var attempt = 0;
        var result = await Retry.Retry.Execute(
            _ =>
            {
                attempt++;
                if (attempt <= 2)
                    throw new TimeoutException("timeout");
                return new ValueTask<string>("done");
            },
            new RetryOptions(
                MaxAttempts: 5,
                Backoff: BackoffType.Constant,
                BaseDelay: TimeSpan.FromMilliseconds(1),
                ShouldRetry: ex => ex is TimeoutException));

        Assert.True(result.IsOk);
        Assert.Equal("done", result.Value);
        Assert.Equal(3, attempt);
    }

    // =========================================================================
    // Execute — cancellation
    // =========================================================================

    [Fact]
    public async Task Execute_returns_cancelled_error_on_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Retry.Retry.Execute(
            _ => new ValueTask<int>(42),
            new RetryOptions(MaxAttempts: 3),
            cts.Token);

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Cancelled, result.Error.Reason);
    }

    [Fact]
    public async Task Execute_cancels_during_delay()
    {
        using var cts = new CancellationTokenSource();
        var attempt = 0;

        // Start the retry and cancel after the first failure
        var result = await Retry.Retry.Execute(
            ct =>
            {
                attempt++;
                if (attempt == 1)
                {
                    // Cancel after the first failure — the delay should be interrupted
                    cts.Cancel();
                    throw new Exception("first attempt");
                }

                return new ValueTask<int>(42);
            },
            new RetryOptions(MaxAttempts: 5, Backoff: BackoffType.Constant, BaseDelay: TimeSpan.FromSeconds(60)),
            cts.Token);

        // The delay should be cancelled — returned as a Cancelled result, not thrown
        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Cancelled, result.Error.Reason);
    }

    // =========================================================================
    // Execute — void overload
    // =========================================================================

    [Fact]
    public async Task Execute_void_succeeds()
    {
        var called = false;
        var result = await Retry.Retry.Execute(
            _ =>
            {
                called = true;
                return ValueTask.CompletedTask;
            },
            new RetryOptions(MaxAttempts: 1));

        Assert.True(result.IsOk);
        Assert.True(called);
    }

    [Fact]
    public async Task Execute_void_retries_on_failure()
    {
        var attempt = 0;
        var result = await Retry.Retry.Execute(
            _ =>
            {
                attempt++;
                if (attempt < 2)
                    throw new Exception("fail");
                return ValueTask.CompletedTask;
            },
            new RetryOptions(MaxAttempts: 3, Backoff: BackoffType.Constant, BaseDelay: TimeSpan.FromMilliseconds(1)));

        Assert.True(result.IsOk);
        Assert.Equal(2, attempt);
    }

    // =========================================================================
    // Default options
    // =========================================================================

    [Fact]
    public async Task Execute_with_default_options()
    {
        var result = await Retry.Retry.Execute(
            _ => new ValueTask<int>(42));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }
}
