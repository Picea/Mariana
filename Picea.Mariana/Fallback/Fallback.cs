// =============================================================================
// Fallback — Resilience Strategy
// =============================================================================
// Provides an alternative value when the primary operation fails.
//
// State machine:
//
//   ┌─────────┐  Succeeded   ┌───────────┐
//   │ Primary │─────────────▶│ Succeeded │  (from primary)
//   └────┬────┘              └───────────┘
//        │ Failed
//        ▼
//   ┌──────────┐  Succeeded  ┌───────────┐
//   │ Fallback │────────────▶│ Succeeded │  (from fallback)
//   └────┬─────┘             └───────────┘
//        │ Failed
//        ▼
//   ┌──────────────┐
//   │ BothFailed   │  (terminal)
//   └──────────────┘
//
// The simplest of the resilience strategies — pure function composition.
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Picea.Mariana.Fallback;

// =============================================================================
// State
// =============================================================================

/// <summary>
/// The state of a fallback automaton.
/// </summary>
public interface FallbackState
{
    /// <summary>Executing the primary operation.</summary>
    record Executing : FallbackState;

    /// <summary>Executing the fallback operation.</summary>
    record ExecutingFallback(Exception PrimaryException) : FallbackState;

    /// <summary>The operation succeeded (from primary or fallback).</summary>
    record Succeeded(bool UsedFallback) : FallbackState;

    /// <summary>Both primary and fallback failed.</summary>
    record BothFailed(Exception PrimaryException, Exception FallbackException) : FallbackState;
}

// =============================================================================
// Events
// =============================================================================

/// <summary>
/// Events that drive the fallback state machine.
/// </summary>
public interface FallbackEvent
{
    /// <summary>The primary operation succeeded.</summary>
    record struct PrimarySucceeded : FallbackEvent;

    /// <summary>The primary operation failed.</summary>
    record struct PrimaryFailed(Exception Exception) : FallbackEvent;

    /// <summary>The fallback operation succeeded.</summary>
    record struct FallbackSucceeded : FallbackEvent;

    /// <summary>The fallback operation failed.</summary>
    record struct FallbackFailed(Exception Exception) : FallbackEvent;
}

// =============================================================================
// Effects
// =============================================================================

/// <summary>
/// Effects produced by the fallback automaton.
/// </summary>
public interface FallbackEffect
{
    /// <summary>No action needed.</summary>
    record struct None : FallbackEffect;

    /// <summary>Execute the primary operation.</summary>
    record struct ExecutePrimary : FallbackEffect;

    /// <summary>Execute the fallback operation.</summary>
    record struct ExecuteFallback : FallbackEffect;

    /// <summary>Report success.</summary>
    record struct ReportSuccess(bool UsedFallback) : FallbackEffect;

    /// <summary>Report that both operations failed.</summary>
    record struct ReportBothFailed(Exception PrimaryException, Exception FallbackException) : FallbackEffect;
}

// =============================================================================
// Automaton
// =============================================================================

/// <summary>
/// A fallback strategy modeled as a Mealy machine automaton.
/// </summary>
public class FallbackAutomaton : Automaton<FallbackState, FallbackEvent, FallbackEffect, Unit>
{
    /// <summary>
    /// Initializes the fallback automaton, ready to execute the primary operation.
    /// </summary>
    public static (FallbackState State, FallbackEffect Effect) Initialize(Unit parameters) =>
        (new FallbackState.Executing(), new FallbackEffect.ExecutePrimary());

    /// <summary>
    /// Pure transition function.
    /// </summary>
    public static (FallbackState State, FallbackEffect Effect) Transition(FallbackState state, FallbackEvent @event) =>
        (state, @event) switch
        {
            (FallbackState.Executing, FallbackEvent.PrimarySucceeded) =>
                (new FallbackState.Succeeded(false), new FallbackEffect.ReportSuccess(false)),

            (FallbackState.Executing, FallbackEvent.PrimaryFailed(var ex)) =>
                (new FallbackState.ExecutingFallback(ex), new FallbackEffect.ExecuteFallback()),

            (FallbackState.ExecutingFallback, FallbackEvent.FallbackSucceeded) =>
                (new FallbackState.Succeeded(true), new FallbackEffect.ReportSuccess(true)),

            (FallbackState.ExecutingFallback(var primaryEx), FallbackEvent.FallbackFailed(var fbEx)) =>
                (new FallbackState.BothFailed(primaryEx, fbEx),
                 new FallbackEffect.ReportBothFailed(primaryEx, fbEx)),

            // Terminal states absorb
            (FallbackState.Succeeded, _) => (state, new FallbackEffect.None()),
            (FallbackState.BothFailed, _) => (state, new FallbackEffect.None()),

            _ => (state, new FallbackEffect.None())
        };
}

// =============================================================================
// Ergonomic API — Fallback.Execute()
// =============================================================================

/// <summary>
/// Ergonomic entry point for the fallback resilience strategy.
/// </summary>
public static class Fallback
{
    /// <summary>
    /// Executes an operation with a fallback alternative.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<Result<T, ResilienceError>> Execute<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        Func<CancellationToken, ValueTask<T>> fallback,
        CancellationToken cancellationToken = default)
    {
        using var activity = ResilienceDiagnostics.Source.StartActivity("Fallback.Execute");

        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag("fallback.used", false);

            return Result<T, ResilienceError>.Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "Operation was cancelled.",
                "Fallback",
                FailureReason.Cancelled));
        }
        catch (Exception primaryEx)
        {
            activity?.SetTag("fallback.primary_exception", primaryEx.GetType().Name);

            try
            {
                var result = await fallback(cancellationToken).ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.SetTag("fallback.used", true);

                return Result<T, ResilienceError>.Ok(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Cancelled during fallback");

                return Result<T, ResilienceError>.Err(new ResilienceError(
                    "Fallback was cancelled.",
                    "Fallback",
                    FailureReason.Cancelled));
            }
            catch (Exception fallbackEx)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Both failed");

                return Result<T, ResilienceError>.Err(new ResilienceError(
                    $"Primary failed: {primaryEx.Message}. Fallback failed: {fallbackEx.Message}",
                    "Fallback",
                    FailureReason.FallbackFailed,
                    fallbackEx));
            }
        }
    }

    /// <summary>
    /// Executes an operation with a static fallback value.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<Result<T, ResilienceError>> Execute<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        T fallbackValue,
        CancellationToken cancellationToken = default) =>
        await Execute(
            operation,
            _ => new ValueTask<T>(fallbackValue),
            cancellationToken).ConfigureAwait(false);
}
