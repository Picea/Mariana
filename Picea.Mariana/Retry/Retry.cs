// =============================================================================
// Retry — Resilience Strategy as a Mealy Machine Automaton
// =============================================================================
// Models the retry lifecycle as a deterministic state machine:
//
//   ┌─────────┐  Succeeded   ┌───────────┐
//   │ Waiting │─────────────▶│ Succeeded │
//   └────┬────┘              └───────────┘
//        │ Failed
//        ▼
//   ┌─────────┐  attempt < max   ┌─────────┐  delay elapsed   ┌─────────┐
//   │ Failed  │─────────────────▶│ Waiting │◀────────────────│ Delaying│
//   │ (check) │                  └─────────┘                  └─────────┘
//   └────┬────┘                       ▲
//        │ attempt >= max             │
//        ▼                            │
//   ┌──────────┐              ┌───────┴──┐
//   │Exhausted │              │ Delaying │
//   └──────────┘              └──────────┘
//
// The automaton is pure — it produces effects (ScheduleRetry, ReportSuccess,
// ReportExhausted) that an interpreter executes.
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Picea.Mariana.Retry;

// =============================================================================
// State
// =============================================================================

/// <summary>
/// The state of a retry automaton.
/// </summary>
public interface RetryState
{
    /// <summary>The current 1-based attempt number.</summary>
    int Attempt { get; }

    /// <summary>Maximum attempts allowed.</summary>
    int MaxAttempts { get; }

    /// <summary>Ready to execute the next attempt.</summary>
    record Waiting(int Attempt, int MaxAttempts) : RetryState;

    /// <summary>Waiting for a backoff delay to elapse before the next attempt.</summary>
    record Delaying(int Attempt, int MaxAttempts, TimeSpan Delay) : RetryState;

    /// <summary>The operation succeeded.</summary>
    record Succeeded(int Attempt, int MaxAttempts) : RetryState;

    /// <summary>All retry attempts have been exhausted.</summary>
    record Exhausted(int Attempt, int MaxAttempts, Exception? LastException) : RetryState;
}

// =============================================================================
// Events
// =============================================================================

/// <summary>
/// Events that drive the retry state machine.
/// </summary>
public interface RetryEvent
{
    /// <summary>The operation attempt succeeded.</summary>
    record struct AttemptSucceeded : RetryEvent;

    /// <summary>The operation attempt failed.</summary>
    record struct AttemptFailed(Exception Exception) : RetryEvent;

    /// <summary>The backoff delay has elapsed — ready for next attempt.</summary>
    record struct DelayElapsed : RetryEvent;
}

// =============================================================================
// Effects
// =============================================================================

/// <summary>
/// Effects produced by the retry automaton for the interpreter to execute.
/// </summary>
public interface RetryEffect
{
    /// <summary>No action needed.</summary>
    record struct None : RetryEffect;

    /// <summary>Execute the operation attempt.</summary>
    record struct ExecuteAttempt(int Attempt) : RetryEffect;

    /// <summary>Schedule a delay before the next retry attempt.</summary>
    record struct ScheduleRetry(int NextAttempt, TimeSpan Delay) : RetryEffect;

    /// <summary>Report that the operation succeeded after retries.</summary>
    record struct ReportSuccess(int TotalAttempts) : RetryEffect;

    /// <summary>Report that all retry attempts have been exhausted.</summary>
    record struct ReportExhausted(int TotalAttempts, Exception? LastException) : RetryEffect;
}

// =============================================================================
// Options
// =============================================================================

/// <summary>
/// Configuration for the retry strategy.
/// </summary>
/// <param name="MaxAttempts">Total number of attempts (including the initial call). Must be ≥ 1.</param>
/// <param name="Backoff">The backoff strategy for computing delay between retries.</param>
/// <param name="BaseDelay">The base delay for backoff computation. Defaults to 1 second.</param>
/// <param name="MaxDelay">Maximum delay cap. Defaults to <see cref="Mariana.Backoff.MaxDelay"/>.</param>
/// <param name="ShouldRetry">Predicate determining whether a given exception should trigger a retry. When null, all exceptions trigger retries.</param>
public record RetryOptions(
    int MaxAttempts = 3,
    BackoffType Backoff = BackoffType.DecorrelatedJitter,
    TimeSpan? BaseDelay = null,
    TimeSpan? MaxDelay = null,
    Func<Exception, bool>? ShouldRetry = null)
{
    /// <summary>The effective base delay (defaults to 1 second).</summary>
    public TimeSpan EffectiveBaseDelay => BaseDelay ?? TimeSpan.FromSeconds(1);

    /// <summary>The effective maximum delay cap.</summary>
    public TimeSpan EffectiveMaxDelay => MaxDelay ?? Mariana.Backoff.MaxDelay;
}

// =============================================================================
// Automaton — pure state machine
// =============================================================================

/// <summary>
/// A retry strategy modeled as a Mealy machine automaton.
/// </summary>
public class RetryAutomaton : Automaton<RetryState, RetryEvent, RetryEffect, RetryOptions>
{
    /// <summary>
    /// Initializes the retry automaton in the Waiting state, ready for the first attempt.
    /// </summary>
    public static (RetryState State, RetryEffect Effect) Initialize(RetryOptions parameters) =>
        (new RetryState.Waiting(1, parameters.MaxAttempts),
         new RetryEffect.ExecuteAttempt(1));

    /// <summary>
    /// Pure transition: given current state and event, produce new state and effect.
    /// </summary>
    public static (RetryState State, RetryEffect Effect) Transition(RetryState state, RetryEvent @event) =>
        (state, @event) switch
        {
            // Attempt succeeded from any active state → terminal success
            (RetryState.Waiting s, RetryEvent.AttemptSucceeded) =>
                (new RetryState.Succeeded(s.Attempt, s.MaxAttempts),
                 new RetryEffect.ReportSuccess(s.Attempt)),

            // Attempt failed, retries remaining → schedule delay
            (RetryState.Waiting s, RetryEvent.AttemptFailed(var ex)) when s.Attempt < s.MaxAttempts =>
                (new RetryState.Delaying(s.Attempt, s.MaxAttempts, TimeSpan.Zero),
                 new RetryEffect.ScheduleRetry(s.Attempt + 1, TimeSpan.Zero)),

            // Attempt failed, no retries remaining → exhausted
            (RetryState.Waiting s, RetryEvent.AttemptFailed(var ex)) =>
                (new RetryState.Exhausted(s.Attempt, s.MaxAttempts, ex),
                 new RetryEffect.ReportExhausted(s.Attempt, ex)),

            // Delay elapsed → ready for next attempt
            (RetryState.Delaying s, RetryEvent.DelayElapsed) =>
                (new RetryState.Waiting(s.Attempt + 1, s.MaxAttempts),
                 new RetryEffect.ExecuteAttempt(s.Attempt + 1)),

            // Terminal states absorb all events (idempotent)
            (RetryState.Succeeded s, _) =>
                (s, new RetryEffect.None()),

            (RetryState.Exhausted s, _) =>
                (s, new RetryEffect.None()),

            _ => (state, new RetryEffect.None())
        };
}

// =============================================================================
// Ergonomic API — Retry.Execute()
// =============================================================================

/// <summary>
/// Ergonomic entry point for the retry resilience strategy.
/// </summary>
public static class Retry
{
    /// <summary>
    /// Executes an operation with retry logic, returning the result or a structured error.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<Result<T, ResilienceError>> Execute<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        RetryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetryOptions();

        ArgumentOutOfRangeException.ThrowIfLessThan(opts.MaxAttempts, 1, nameof(opts.MaxAttempts));

        using var activity = ResilienceDiagnostics.Source.StartActivity("Retry.Execute");
        activity?.SetTag("retry.max_attempts", opts.MaxAttempts);
        activity?.SetTag("retry.backoff", opts.Backoff.ToString());

        Exception? lastException = null;

        for (var attempt = 1; attempt <= opts.MaxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

                return Result<T, ResilienceError>.Err(new ResilienceError(
                    "Operation was cancelled.",
                    "Retry",
                    FailureReason.Cancelled));
            }

            using var attemptActivity = ResilienceDiagnostics.Source.StartActivity("Retry.Attempt");
            attemptActivity?.SetTag("retry.attempt", attempt);

            try
            {
                var result = await operation(cancellationToken).ConfigureAwait(false);

                attemptActivity?.SetStatus(ActivityStatusCode.Ok);
                activity?.SetTag("retry.total_attempts", attempt);
                activity?.SetStatus(ActivityStatusCode.Ok);

                return Result<T, ResilienceError>.Ok(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

                return Result<T, ResilienceError>.Err(new ResilienceError(
                    "Operation was cancelled.",
                    "Retry",
                    FailureReason.Cancelled));
            }
            catch (Exception ex)
            {
                lastException = ex;

                attemptActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                attemptActivity?.SetTag("retry.exception.type", ex.GetType().Name);

                if (opts.ShouldRetry is not null && !opts.ShouldRetry(ex))
                {
                    activity?.SetTag("retry.total_attempts", attempt);
                    activity?.SetStatus(ActivityStatusCode.Error, "Non-retryable exception");

                    return Result<T, ResilienceError>.Err(new ResilienceError(
                        $"Non-retryable exception on attempt {attempt}: {ex.Message}",
                        "Retry",
                        FailureReason.Unknown,
                        ex));
                }

                if (attempt < opts.MaxAttempts)
                {
                    var delay = Backoff.Compute(
                        opts.Backoff,
                        opts.EffectiveBaseDelay,
                        attempt,
                        opts.EffectiveMaxDelay);

                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

                            return Result<T, ResilienceError>.Err(new ResilienceError(
                                "Operation was cancelled.",
                                "Retry",
                                FailureReason.Cancelled));
                        }
                    }
                }
            }
        }

        activity?.SetTag("retry.total_attempts", opts.MaxAttempts);
        activity?.SetStatus(ActivityStatusCode.Error, "Retries exhausted");

        return Result<T, ResilienceError>.Err(new ResilienceError(
            $"All {opts.MaxAttempts} attempt(s) failed. Last exception: {lastException?.Message}",
            "Retry",
            FailureReason.RetriesExhausted,
            lastException));
    }

    /// <summary>
    /// Executes a void operation with retry logic.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<Result<Unit, ResilienceError>> Execute(
        Func<CancellationToken, ValueTask> operation,
        RetryOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await Execute(async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return Unit.Value;
        }, options, cancellationToken).ConfigureAwait(false);
}
