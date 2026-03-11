// =============================================================================
// Rate Limiter — Resilience Strategy as a Mealy Machine Automaton
// =============================================================================
// Controls throughput to protect downstream services using a token bucket
// algorithm (Tanenbaum, Computer Networks, 1981).
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Picea.Mariana.RateLimiter;

// =============================================================================
// State
// =============================================================================

/// <summary>
/// The state of a rate limiter automaton.
/// </summary>
public interface RateLimiterState
{
    /// <summary>Tokens available — requests are permitted.</summary>
    record Available(int Tokens, int PermitLimit) : RateLimiterState;

    /// <summary>No tokens available — requests are rejected.</summary>
    record Depleted(int PermitLimit) : RateLimiterState;
}

// =============================================================================
// Events
// =============================================================================

/// <summary>
/// Events that drive the rate limiter state machine.
/// </summary>
public interface RateLimiterEvent
{
    /// <summary>A request is attempted.</summary>
    record struct RequestAttempted : RateLimiterEvent;

    /// <summary>The replenishment timer has fired — add tokens.</summary>
    record struct TokensReplenished(int Count) : RateLimiterEvent;
}

// =============================================================================
// Effects
// =============================================================================

/// <summary>
/// Effects produced by the rate limiter automaton.
/// </summary>
public interface RateLimiterEffect
{
    /// <summary>No action needed.</summary>
    record struct None : RateLimiterEffect;

    /// <summary>Permit the request.</summary>
    record struct Permit : RateLimiterEffect;

    /// <summary>Reject the request — rate limit exceeded.</summary>
    record struct Reject : RateLimiterEffect;
}

// =============================================================================
// Options
// =============================================================================

/// <summary>
/// Configuration for the rate limiter strategy.
/// </summary>
/// <param name="PermitLimit">Maximum number of concurrent permits. Defaults to 100.</param>
/// <param name="Window">Time window for replenishment. Defaults to 1 second.</param>
/// <param name="TokensPerPeriod">Tokens added per replenishment period. Defaults to PermitLimit.</param>
public record RateLimiterOptions(
    int PermitLimit = 100,
    TimeSpan? Window = null,
    int? TokensPerPeriod = null)
{
    /// <summary>The effective time window (defaults to 1 second).</summary>
    public TimeSpan EffectiveWindow => Window ?? TimeSpan.FromSeconds(1);

    /// <summary>The effective tokens per period (defaults to PermitLimit).</summary>
    public int EffectiveTokensPerPeriod => TokensPerPeriod ?? PermitLimit;
}

// =============================================================================
// Automaton
// =============================================================================

/// <summary>
/// A rate limiter modeled as a Mealy machine automaton (token bucket).
/// </summary>
public class RateLimiterAutomaton : Automaton<RateLimiterState, RateLimiterEvent, RateLimiterEffect, RateLimiterOptions>
{
    /// <summary>
    /// Initializes the rate limiter with a full token bucket.
    /// </summary>
    public static (RateLimiterState State, RateLimiterEffect Effect) Initialize(RateLimiterOptions parameters) =>
        (new RateLimiterState.Available(parameters.PermitLimit, parameters.PermitLimit),
         new RateLimiterEffect.None());

    /// <summary>
    /// Pure transition function.
    /// </summary>
    public static (RateLimiterState State, RateLimiterEffect Effect) Transition(
        RateLimiterState state, RateLimiterEvent @event) =>
        (state, @event) switch
        {
            (RateLimiterState.Available(var tokens, var limit), RateLimiterEvent.RequestAttempted) when tokens > 1 =>
                (new RateLimiterState.Available(tokens - 1, limit), new RateLimiterEffect.Permit()),

            (RateLimiterState.Available(1, var limit), RateLimiterEvent.RequestAttempted) =>
                (new RateLimiterState.Depleted(limit), new RateLimiterEffect.Permit()),

            (RateLimiterState.Available(0, _), RateLimiterEvent.RequestAttempted) =>
                (state, new RateLimiterEffect.Reject()),

            (RateLimiterState.Depleted, RateLimiterEvent.RequestAttempted) =>
                (state, new RateLimiterEffect.Reject()),

            (RateLimiterState.Available(var tokens, var limit), RateLimiterEvent.TokensReplenished(var count)) =>
                (new RateLimiterState.Available(Math.Min(tokens + count, limit), limit),
                 new RateLimiterEffect.None()),

            (RateLimiterState.Depleted(var limit), RateLimiterEvent.TokensReplenished(var count)) =>
                (new RateLimiterState.Available(Math.Min(count, limit), limit),
                 new RateLimiterEffect.None()),

            _ => (state, new RateLimiterEffect.None())
        };
}

// =============================================================================
// Ergonomic API — RateLimiter instance
// =============================================================================

/// <summary>
/// A stateful rate limiter that tracks token availability across calls.
/// </summary>
public sealed class RateLimiter : IDisposable
{
    private readonly RateLimiterOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _replenishTimer;
    private int _tokens;

    /// <summary>
    /// The current number of available tokens.
    /// </summary>
    public int AvailableTokens => _tokens;

    /// <summary>
    /// Creates a new rate limiter with the specified options.
    /// </summary>
    public RateLimiter(RateLimiterOptions? options = null)
    {
        _options = options ?? new RateLimiterOptions();
        _tokens = _options.PermitLimit;

        _replenishTimer = new Timer(
            _ => Replenish(),
            null,
            _options.EffectiveWindow,
            _options.EffectiveWindow);
    }

    /// <summary>
    /// Attempts to acquire a permit and execute the operation.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<Result<T, ResilienceError>> Execute<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        using var activity = ResilienceDiagnostics.Source.StartActivity("RateLimiter.Execute");
        activity?.SetTag("rate_limiter.permit_limit", _options.PermitLimit);

        if (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "Operation was cancelled.",
                "RateLimiter",
                FailureReason.Cancelled));
        }

        bool permitted;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_tokens > 0)
                {
                    _tokens--;
                    permitted = true;
                }
                else
                {
                    permitted = false;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "Operation was cancelled.",
                "RateLimiter",
                FailureReason.Cancelled));
        }

        if (!permitted)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Rate limited");
            activity?.SetTag("rate_limiter.permitted", false);

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "Rate limit exceeded. Try again later.",
                "RateLimiter",
                FailureReason.RateLimited));
        }

        activity?.SetTag("rate_limiter.permitted", true);

        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return Result<T, ResilienceError>.Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "Operation was cancelled.",
                "RateLimiter",
                FailureReason.Cancelled));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            return Result<T, ResilienceError>.Err(new ResilienceError(
                $"Operation failed: {ex.Message}",
                "RateLimiter",
                FailureReason.Unknown,
                ex));
        }
    }

    private void Replenish()
    {
        _gate.Wait();
        try
        {
            _tokens = Math.Min(_tokens + _options.EffectiveTokensPerPeriod, _options.PermitLimit);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _replenishTimer.Dispose();
        _gate.Dispose();
    }
}
