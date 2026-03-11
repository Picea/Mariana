// =============================================================================
// Timeout — Resilience Strategy as a Mealy Machine Automaton
// =============================================================================
// Models the timeout lifecycle as a deterministic state machine:
//
//   ┌──────────┐  Completed   ┌───────────┐
//   │ Running  │─────────────▶│ Completed │
//   └────┬─────┘              └───────────┘
//        │ DeadlineExceeded
//        ▼
//   ┌──────────┐
//   │ TimedOut │  (terminal — cancels the operation)
//   └──────────┘
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Picea.Mariana.Timeout;

// =============================================================================
// State
// =============================================================================

/// <summary>
/// The state of a timeout automaton.
/// </summary>
public interface TimeoutState
{
    /// <summary>The configured timeout duration.</summary>
    TimeSpan Duration { get; }

    /// <summary>The operation is running within the deadline.</summary>
    record Running(TimeSpan Duration) : TimeoutState;

    /// <summary>The operation completed before the deadline.</summary>
    record Completed(TimeSpan Duration) : TimeoutState;

    /// <summary>The operation exceeded the deadline and was cancelled.</summary>
    record TimedOut(TimeSpan Duration) : TimeoutState;
}

// =============================================================================
// Events
// =============================================================================

/// <summary>
/// Events that drive the timeout state machine.
/// </summary>
public interface TimeoutEvent
{
    /// <summary>The operation completed (successfully or with an error) before the deadline.</summary>
    record struct OperationCompleted : TimeoutEvent;

    /// <summary>The deadline was exceeded.</summary>
    record struct DeadlineExceeded : TimeoutEvent;
}

// =============================================================================
// Effects
// =============================================================================

/// <summary>
/// Effects produced by the timeout automaton.
/// </summary>
public interface TimeoutEffect
{
    /// <summary>No action needed.</summary>
    record struct None : TimeoutEffect;

    /// <summary>Start the operation with a deadline timer.</summary>
    record struct StartTimer(TimeSpan Duration) : TimeoutEffect;

    /// <summary>Report that the operation completed within the deadline.</summary>
    record struct ReportCompleted : TimeoutEffect;

    /// <summary>Report that the operation timed out.</summary>
    record struct ReportTimedOut(TimeSpan Duration) : TimeoutEffect;
}

// =============================================================================
// Options
// =============================================================================

/// <summary>
/// Configuration for the timeout strategy.
/// </summary>
/// <param name="Duration">The maximum time allowed for the operation. Defaults to 30 seconds.</param>
public record TimeoutOptions(TimeSpan? Duration = null)
{
    /// <summary>The effective timeout duration (defaults to 30 seconds).</summary>
    public TimeSpan EffectiveDuration => Duration ?? TimeSpan.FromSeconds(30);
}

// =============================================================================
// Automaton — pure state machine
// =============================================================================

/// <summary>
/// A timeout strategy modeled as a Mealy machine automaton.
/// </summary>
public class TimeoutAutomaton : Automaton<TimeoutState, TimeoutEvent, TimeoutEffect, TimeoutOptions>
{
    /// <summary>
    /// Initializes the timeout automaton in the Running state.
    /// </summary>
    public static (TimeoutState State, TimeoutEffect Effect) Initialize(TimeoutOptions parameters) =>
        (new TimeoutState.Running(parameters.EffectiveDuration),
         new TimeoutEffect.StartTimer(parameters.EffectiveDuration));

    /// <summary>
    /// Pure transition function.
    /// </summary>
    public static (TimeoutState State, TimeoutEffect Effect) Transition(TimeoutState state, TimeoutEvent @event) =>
        (state, @event) switch
        {
            (TimeoutState.Running s, TimeoutEvent.OperationCompleted) =>
                (new TimeoutState.Completed(s.Duration),
                 new TimeoutEffect.ReportCompleted()),

            (TimeoutState.Running s, TimeoutEvent.DeadlineExceeded) =>
                (new TimeoutState.TimedOut(s.Duration),
                 new TimeoutEffect.ReportTimedOut(s.Duration)),

            // Terminal states absorb all events
            (TimeoutState.Completed s, _) => (s, new TimeoutEffect.None()),
            (TimeoutState.TimedOut s, _) => (s, new TimeoutEffect.None()),

            _ => (state, new TimeoutEffect.None())
        };
}

// =============================================================================
// Ergonomic API — Timeout.Execute()
// =============================================================================

/// <summary>
/// Ergonomic entry point for the timeout resilience strategy.
/// </summary>
public static class Timeout
{
    /// <summary>
    /// Executes an operation with a timeout deadline.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<Result<T, ResilienceError>> Execute<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        TimeoutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new TimeoutOptions();
        var duration = opts.EffectiveDuration;

        using var activity = ResilienceDiagnostics.Source.StartActivity("Timeout.Execute");
        activity?.SetTag("timeout.duration_ms", duration.TotalMilliseconds);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(duration);

        try
        {
            var result = await operation(linkedCts.Token).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);

            return Result<T, ResilienceError>.Ok(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Timeout");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                $"Operation timed out after {duration.TotalMilliseconds}ms.",
                "Timeout",
                FailureReason.Timeout));
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "Operation was cancelled.",
                "Timeout",
                FailureReason.Cancelled));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            return Result<T, ResilienceError>.Err(new ResilienceError(
                $"Operation failed: {ex.Message}",
                "Timeout",
                FailureReason.Unknown,
                ex));
        }
    }

    /// <summary>
    /// Executes a void operation with a timeout deadline.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<Result<Unit, ResilienceError>> Execute(
        Func<CancellationToken, ValueTask> operation,
        TimeoutOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await Execute(async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return Unit.Value;
        }, options, cancellationToken).ConfigureAwait(false);
}
