using Picea.Mariana.RateLimiter;

namespace Picea.Mariana.Tests;

public sealed class RateLimiterTests : IDisposable
{
    private readonly RateLimiter.RateLimiter _rateLimiter;

    public RateLimiterTests() =>
        _rateLimiter = new RateLimiter.RateLimiter(new RateLimiterOptions(
            PermitLimit: 3,
            Window: TimeSpan.FromHours(1))); // Long window so replenishment doesn't fire during tests

    public void Dispose() => _rateLimiter.Dispose();

    [Fact]
    public async Task Permits_requests_within_limit()
    {
        var result = await _rateLimiter.Execute(
            _ => ValueTask.FromResult(42));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
        Assert.Equal(2, _rateLimiter.AvailableTokens);
    }

    [Fact]
    public async Task Rejects_requests_when_tokens_exhausted()
    {
        for (var i = 0; i < 3; i++)
        {
            var ok = await _rateLimiter.Execute(
                _ => ValueTask.FromResult(i));
            Assert.True(ok.IsOk);
        }

        Assert.Equal(0, _rateLimiter.AvailableTokens);

        var rejected = await _rateLimiter.Execute(
            _ => ValueTask.FromResult(99));

        Assert.True(rejected.IsErr);
        Assert.Equal(FailureReason.RateLimited, rejected.Error.Reason);
    }

    [Fact]
    public async Task Permits_resume_after_replenishment()
    {
        using var limiter = new RateLimiter.RateLimiter(new RateLimiterOptions(
            PermitLimit: 1,
            Window: TimeSpan.FromMilliseconds(50)));

        var first = await limiter.Execute(
            _ => ValueTask.FromResult(1));
        Assert.True(first.IsOk);

        var rejected = await limiter.Execute(
            _ => ValueTask.FromResult(2));
        Assert.True(rejected.IsErr);

        // Wait for replenishment
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        var afterReplenish = await limiter.Execute(
            _ => ValueTask.FromResult(3));
        Assert.True(afterReplenish.IsOk);
        Assert.Equal(3, afterReplenish.Value);
    }

    [Fact]
    public async Task Returns_err_when_operation_throws()
    {
        var result = await _rateLimiter.Execute<int>(
            _ => throw new InvalidOperationException("boom"));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Unknown, result.Error.Reason);
        Assert.Contains("boom", result.Error.Message);
    }

    [Fact]
    public async Task Returns_err_on_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _rateLimiter.Execute(
            async (ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return 42;
            },
            cts.Token);

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Cancelled, result.Error.Reason);
    }

    [Fact]
    public void Default_options_have_sensible_values()
    {
        var options = new RateLimiterOptions();

        Assert.Equal(100, options.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(1), options.EffectiveWindow);
        Assert.Equal(100, options.EffectiveTokensPerPeriod);
    }

    [Fact]
    public void Tokens_per_period_can_differ_from_permit_limit()
    {
        var options = new RateLimiterOptions(
            PermitLimit: 10,
            TokensPerPeriod: 5);

        Assert.Equal(10, options.PermitLimit);
        Assert.Equal(5, options.EffectiveTokensPerPeriod);
    }
}

public class RateLimiterAutomatonTests
{
    [Fact]
    public void Initialize_starts_with_full_bucket()
    {
        var (state, effect) = RateLimiterAutomaton.Initialize(new RateLimiterOptions(PermitLimit: 10));

        Assert.IsType<RateLimiterState.Available>(state);
        var available = (RateLimiterState.Available)state;
        Assert.Equal(10, available.Tokens);
        Assert.Equal(10, available.PermitLimit);
        Assert.IsType<RateLimiterEffect.None>(effect);
    }

    [Fact]
    public void Request_consumes_token()
    {
        var initial = new RateLimiterState.Available(5, 10);
        var (state, effect) = RateLimiterAutomaton.Transition(initial, new RateLimiterEvent.RequestAttempted());

        Assert.IsType<RateLimiterState.Available>(state);
        Assert.Equal(4, ((RateLimiterState.Available)state).Tokens);
        Assert.IsType<RateLimiterEffect.Permit>(effect);
    }

    [Fact]
    public void Last_token_transitions_to_depleted()
    {
        var initial = new RateLimiterState.Available(1, 10);
        var (state, effect) = RateLimiterAutomaton.Transition(initial, new RateLimiterEvent.RequestAttempted());

        Assert.IsType<RateLimiterState.Depleted>(state);
        Assert.IsType<RateLimiterEffect.Permit>(effect);
    }

    [Fact]
    public void Depleted_rejects_requests()
    {
        var depleted = new RateLimiterState.Depleted(10);
        var (state, effect) = RateLimiterAutomaton.Transition(depleted, new RateLimiterEvent.RequestAttempted());

        Assert.IsType<RateLimiterState.Depleted>(state);
        Assert.IsType<RateLimiterEffect.Reject>(effect);
    }

    [Fact]
    public void Replenishment_restores_tokens_from_depleted()
    {
        var depleted = new RateLimiterState.Depleted(10);
        var (state, effect) = RateLimiterAutomaton.Transition(
            depleted, new RateLimiterEvent.TokensReplenished(5));

        Assert.IsType<RateLimiterState.Available>(state);
        Assert.Equal(5, ((RateLimiterState.Available)state).Tokens);
        Assert.IsType<RateLimiterEffect.None>(effect);
    }

    [Fact]
    public void Replenishment_caps_at_permit_limit()
    {
        var available = new RateLimiterState.Available(8, 10);
        var (state, _) = RateLimiterAutomaton.Transition(
            available, new RateLimiterEvent.TokensReplenished(5));

        Assert.IsType<RateLimiterState.Available>(state);
        Assert.Equal(10, ((RateLimiterState.Available)state).Tokens);
    }

    [Fact]
    public void Full_lifecycle_consume_replenish()
    {
        var (state, _) = RateLimiterAutomaton.Initialize(new RateLimiterOptions(PermitLimit: 2));

        // Consume both tokens
        (state, _) = RateLimiterAutomaton.Transition(state, new RateLimiterEvent.RequestAttempted());
        Assert.IsType<RateLimiterState.Available>(state);

        (state, var effect) = RateLimiterAutomaton.Transition(state, new RateLimiterEvent.RequestAttempted());
        Assert.IsType<RateLimiterState.Depleted>(state);
        Assert.IsType<RateLimiterEffect.Permit>(effect);

        // Reject
        (state, effect) = RateLimiterAutomaton.Transition(state, new RateLimiterEvent.RequestAttempted());
        Assert.IsType<RateLimiterEffect.Reject>(effect);

        // Replenish
        (state, _) = RateLimiterAutomaton.Transition(state, new RateLimiterEvent.TokensReplenished(2));
        Assert.IsType<RateLimiterState.Available>(state);
        Assert.Equal(2, ((RateLimiterState.Available)state).Tokens);
    }
}
