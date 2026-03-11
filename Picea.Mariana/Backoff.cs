// =============================================================================
// Backoff — delay calculation strategies for retry
// =============================================================================
// Implements the standard delay families from resilience engineering:
//
//   Constant:    Δ = base
//   Linear:      Δ = base × attempt
//   Exponential: Δ = base × 2^(attempt-1), capped at MaxDelay
//   DecorrelatedJitter: Δ ~ Uniform(0, min(cap, base × 3^(attempt-1)))
//
// The decorrelated jitter algorithm is from:
//   Vollmer, M. et al. (2019). "Exponential Backoff and Jitter" — AWS Architecture Blog
//   https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/
//
// Decorrelated jitter is the recommended default. It provides:
//   - Better spread than full jitter (avoids thundering herd)
//   - Bounded growth via MaxDelay cap
//   - Independent samples per caller (no coordination needed)
// =============================================================================

namespace Picea.Mariana;

/// <summary>
/// The family of delay functions available for retry backoff.
/// </summary>
public enum BackoffType
{
    /// <summary>Fixed delay between attempts: Δ = base.</summary>
    Constant,

    /// <summary>Linearly increasing delay: Δ = base × attempt.</summary>
    Linear,

    /// <summary>Exponentially increasing delay: Δ = base × 2^(attempt-1), capped.</summary>
    Exponential,

    /// <summary>
    /// Decorrelated jitter (recommended). Randomly distributed delays
    /// that prevent thundering herd synchronization.
    /// </summary>
    DecorrelatedJitter
}

/// <summary>
/// Computes delay durations for retry backoff strategies.
/// </summary>
/// <remarks>
/// <para>
/// All calculations are pure functions — no mutable state, no side effects
/// beyond the thread-static <see cref="Random"/> instance for jitter.
/// </para>
/// <para>
/// The <see cref="MaxDelay"/> cap prevents unbounded growth in exponential
/// and jitter strategies. Without a cap, exponential backoff can produce
/// delays of hours after ~30 attempts.
/// </para>
/// </remarks>
public static class Backoff
{
    /// <summary>
    /// Default maximum delay cap to prevent unbounded growth.
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    [ThreadStatic]
    private static Random? _random;

    private static Random ThreadRandom => _random ??= new Random();

    /// <summary>
    /// Computes the delay for a given attempt using the specified backoff strategy.
    /// </summary>
    /// <param name="type">The backoff strategy to use.</param>
    /// <param name="baseDelay">The base delay duration.</param>
    /// <param name="attempt">The 1-based attempt number (1 = first retry).</param>
    /// <param name="maxDelay">Optional maximum delay cap. Defaults to <see cref="MaxDelay"/>.</param>
    /// <returns>The computed delay duration, never exceeding <paramref name="maxDelay"/>.</returns>
    public static TimeSpan Compute(BackoffType type, TimeSpan baseDelay, int attempt, TimeSpan? maxDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1, nameof(attempt));

        var cap = maxDelay ?? MaxDelay;

        var delay = type switch
        {
            BackoffType.Constant => baseDelay,
            BackoffType.Linear => baseDelay * attempt,
            BackoffType.Exponential => ComputeExponential(baseDelay, attempt),
            BackoffType.DecorrelatedJitter => ComputeDecorrelatedJitter(baseDelay, attempt, cap),
            _ => baseDelay
        };

        return delay > cap ? cap : delay;
    }

    private static TimeSpan ComputeExponential(TimeSpan baseDelay, int attempt)
    {
        // base × 2^(attempt-1), using bit shift for integer powers of 2
        var multiplier = 1L << Math.Min(attempt - 1, 30);

        return baseDelay * multiplier;
    }

    private static TimeSpan ComputeDecorrelatedJitter(TimeSpan baseDelay, int attempt, TimeSpan cap)
    {
        // Decorrelated jitter: Uniform(0, min(cap, base × 3^(attempt-1)))
        // Uses double arithmetic to avoid overflow, then clamps.
        var upperBound = baseDelay.TotalMilliseconds * Math.Pow(3, attempt - 1);
        var cappedMs = Math.Min(upperBound, cap.TotalMilliseconds);
        var jitteredMs = ThreadRandom.NextDouble() * cappedMs;

        return TimeSpan.FromMilliseconds(jitteredMs);
    }
}
