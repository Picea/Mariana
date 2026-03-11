// =============================================================================
// ResilienceError — structured error type for resilience strategies
// =============================================================================
// Captures the strategy that failed, the reason, and optionally the last
// exception encountered. Used as the error channel of Result<T, ResilienceError>.
// =============================================================================

namespace Picea.Mariana;

/// <summary>
/// A structured error from a resilience strategy execution.
/// </summary>
/// <remarks>
/// <para>
/// Resilience errors are values, not exceptions. They propagate through
/// <see cref="Result{TSuccess,TError}"/>, giving callers composable error handling.
/// </para>
/// <para>
/// The <see cref="Reason"/> discriminated union provides machine-readable failure
/// classification, while <see cref="Message"/> provides a human-readable description.
/// </para>
/// </remarks>
/// <param name="Message">Human-readable description of the failure.</param>
/// <param name="Strategy">The resilience strategy that produced the error (e.g., "Retry", "CircuitBreaker").</param>
/// <param name="Reason">Machine-readable failure classification.</param>
/// <param name="Exception">The last exception encountered, if any.</param>
public readonly record struct ResilienceError(
    string Message,
    string Strategy,
    FailureReason Reason = FailureReason.Unknown,
    Exception? Exception = null)
{
    /// <inheritdoc/>
    public override string ToString() =>
        $"[{Strategy}:{Reason}] {Message}";
}

/// <summary>
/// Machine-readable classification of why a resilience strategy failed.
/// </summary>
public enum FailureReason
{
    /// <summary>Unclassified failure.</summary>
    Unknown = 0,

    /// <summary>All retry attempts were exhausted.</summary>
    RetriesExhausted,

    /// <summary>The operation exceeded the configured timeout.</summary>
    Timeout,

    /// <summary>The circuit breaker is open — calls are being rejected.</summary>
    CircuitOpen,

    /// <summary>The rate limiter rejected the request — throughput limit reached.</summary>
    RateLimited,

    /// <summary>The primary operation and the fallback both failed.</summary>
    FallbackFailed,

    /// <summary>All hedged attempts failed.</summary>
    HedgingFailed,

    /// <summary>The operation was cancelled via CancellationToken.</summary>
    Cancelled
}
