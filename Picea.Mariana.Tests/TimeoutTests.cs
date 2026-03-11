using Picea.Mariana.Timeout;

namespace Picea.Mariana.Tests;

public class TimeoutTests
{
    // =========================================================================
    // Execute — success cases
    // =========================================================================

    [Fact]
    public async Task Execute_succeeds_within_deadline()
    {
        var result = await Timeout.Timeout.Execute(
            _ => new ValueTask<int>(42),
            new TimeoutOptions(Duration: TimeSpan.FromSeconds(5)));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task Execute_with_default_options()
    {
        var result = await Timeout.Timeout.Execute(
            _ => new ValueTask<string>("done"));

        Assert.True(result.IsOk);
        Assert.Equal("done", result.Value);
    }

    // =========================================================================
    // Execute — timeout cases
    // =========================================================================

    [Fact]
    public async Task Execute_returns_timeout_error_when_deadline_exceeded()
    {
        var result = await Timeout.Timeout.Execute(
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return 42;
            },
            new TimeoutOptions(Duration: TimeSpan.FromMilliseconds(50)));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Timeout, result.Error.Reason);
        Assert.Equal("Timeout", result.Error.Strategy);
    }

    // =========================================================================
    // Execute — external cancellation
    // =========================================================================

    [Fact]
    public async Task Execute_returns_cancelled_on_external_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Timeout.Timeout.Execute(
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                return 42;
            },
            new TimeoutOptions(Duration: TimeSpan.FromSeconds(30)),
            cts.Token);

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Cancelled, result.Error.Reason);
    }

    // =========================================================================
    // Execute — operation throws
    // =========================================================================

    [Fact]
    public async Task Execute_returns_error_when_operation_throws()
    {
        var result = await Timeout.Timeout.Execute<int>(
            _ => throw new InvalidOperationException("boom"),
            new TimeoutOptions(Duration: TimeSpan.FromSeconds(5)));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Unknown, result.Error.Reason);
        Assert.IsType<InvalidOperationException>(result.Error.Exception);
    }

    // =========================================================================
    // Execute — void overload
    // =========================================================================

    [Fact]
    public async Task Execute_void_succeeds()
    {
        var called = false;
        var result = await Timeout.Timeout.Execute(
            _ =>
            {
                called = true;
                return ValueTask.CompletedTask;
            },
            new TimeoutOptions(Duration: TimeSpan.FromSeconds(5)));

        Assert.True(result.IsOk);
        Assert.True(called);
    }

    [Fact]
    public async Task Execute_void_returns_timeout()
    {
        var result = await Timeout.Timeout.Execute(
            async ct => await Task.Delay(TimeSpan.FromSeconds(10), ct),
            new TimeoutOptions(Duration: TimeSpan.FromMilliseconds(50)));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Timeout, result.Error.Reason);
    }
}
