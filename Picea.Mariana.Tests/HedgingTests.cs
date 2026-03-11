using Picea.Mariana.Hedging;

namespace Picea.Mariana.Tests;

public class HedgingTests
{
    [Fact]
    public async Task Returns_primary_result_when_successful()
    {
        var result = await Hedging.Hedging.Execute(
            (attempt, _) => ValueTask.FromResult(42 + attempt),
            new HedgingOptions(MaxHedgedAttempts: 2, Delay: TimeSpan.FromSeconds(5)));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value); // Primary (attempt 0) returns first
    }

    [Fact]
    public async Task Returns_hedged_result_when_primary_fails()
    {
        var callCount = 0;
        var result = await Hedging.Hedging.Execute(
            async (attempt, ct) =>
            {
                Interlocked.Increment(ref callCount);
                if (attempt == 0)
                    throw new InvalidOperationException("Primary failed");

                return 99;
            },
            new HedgingOptions(MaxHedgedAttempts: 2, Delay: TimeSpan.FromMilliseconds(10)));

        Assert.True(result.IsOk);
        Assert.Equal(99, result.Value);
    }

    [Fact]
    public async Task Returns_err_when_all_attempts_fail()
    {
        var result = await Hedging.Hedging.Execute<int>(
            (_, _) => throw new InvalidOperationException("always fails"),
            new HedgingOptions(MaxHedgedAttempts: 3, Delay: TimeSpan.FromMilliseconds(10)));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.HedgingFailed, result.Error.Reason);
        Assert.Contains("3 hedged attempt(s) failed", result.Error.Message);
    }

    [Fact]
    public async Task Cancels_remaining_on_first_success()
    {
        var cancellationObserved = false;

        var result = await Hedging.Hedging.Execute(
            async (attempt, ct) =>
            {
                if (attempt == 0)
                {
                    // Primary is slow
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved = true;
                        throw;
                    }
                    return -1;
                }

                // Hedged attempt succeeds immediately
                return 42;
            },
            new HedgingOptions(MaxHedgedAttempts: 2, Delay: TimeSpan.FromMilliseconds(10)));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);

        // Give a moment for the cancellation to propagate
        await Task.Delay(50);
        Assert.True(cancellationObserved, "Primary request should have been cancelled");
    }

    [Fact]
    public async Task Returns_err_on_external_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Hedging.Hedging.Execute(
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return 42;
            },
            new HedgingOptions(MaxHedgedAttempts: 2),
            cts.Token);

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Cancelled, result.Error.Reason);
    }

    [Fact]
    public void Default_options_have_sensible_values()
    {
        var options = new HedgingOptions();

        Assert.Equal(2, options.MaxHedgedAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.EffectiveDelay);
    }

    [Fact]
    public async Task Single_attempt_behaves_like_direct_call()
    {
        var result = await Hedging.Hedging.Execute(
            (_, _) => ValueTask.FromResult(42),
            new HedgingOptions(MaxHedgedAttempts: 1));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task Single_attempt_failure_returns_err()
    {
        var result = await Hedging.Hedging.Execute<int>(
            (_, _) => throw new InvalidOperationException("boom"),
            new HedgingOptions(MaxHedgedAttempts: 1, Delay: TimeSpan.FromMilliseconds(10)));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.HedgingFailed, result.Error.Reason);
    }
}
