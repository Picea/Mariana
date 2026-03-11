namespace Picea.Mariana.Tests;

public class FallbackTests
{
    // =========================================================================
    // Execute — primary succeeds
    // =========================================================================

    [Fact]
    public async Task Execute_returns_primary_result_on_success()
    {
        var result = await Fallback.Fallback.Execute(
            _ => new ValueTask<int>(42),
            _ => new ValueTask<int>(99));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task Execute_does_not_invoke_fallback_on_success()
    {
        var fallbackCalled = false;

        await Fallback.Fallback.Execute(
            _ => new ValueTask<int>(42),
            _ =>
            {
                fallbackCalled = true;
                return new ValueTask<int>(99);
            });

        Assert.False(fallbackCalled);
    }

    // =========================================================================
    // Execute — fallback on primary failure
    // =========================================================================

    [Fact]
    public async Task Execute_returns_fallback_result_on_primary_failure()
    {
        var result = await Fallback.Fallback.Execute(
            _ => throw new Exception("primary fail"),
            _ => new ValueTask<int>(99));

        Assert.True(result.IsOk);
        Assert.Equal(99, result.Value);
    }

    // =========================================================================
    // Execute — both fail
    // =========================================================================

    [Fact]
    public async Task Execute_returns_error_when_both_fail()
    {
        var result = await Fallback.Fallback.Execute<int>(
            _ => throw new Exception("primary fail"),
            _ => throw new Exception("fallback fail"));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.FallbackFailed, result.Error.Reason);
        Assert.Equal("Fallback", result.Error.Strategy);
        Assert.Contains("Primary failed", result.Error.Message);
        Assert.Contains("Fallback failed", result.Error.Message);
    }

    // =========================================================================
    // Execute — static fallback value
    // =========================================================================

    [Fact]
    public async Task Execute_with_static_fallback_value()
    {
        var result = await Fallback.Fallback.Execute(
            _ => throw new Exception("primary fail"),
            fallbackValue: -1);

        Assert.True(result.IsOk);
        Assert.Equal(-1, result.Value);
    }

    [Fact]
    public async Task Execute_with_static_fallback_not_used_on_success()
    {
        var result = await Fallback.Fallback.Execute(
            _ => new ValueTask<int>(42),
            fallbackValue: -1);

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    // =========================================================================
    // Cancellation
    // =========================================================================

    [Fact]
    public async Task Execute_returns_cancelled_on_primary_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Fallback.Fallback.Execute(
            async ct =>
            {
                await Task.Delay(1000, ct);
                return 42;
            },
            _ => new ValueTask<int>(99),
            cts.Token);

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Cancelled, result.Error.Reason);
    }
}
