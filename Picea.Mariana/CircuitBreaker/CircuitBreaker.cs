// =============================================================================
// Circuit Breaker — Resilience Strategy as a Mealy Machine Automaton
// =============================================================================
// Models the circuit breaker lifecycle as a deterministic state machine:
//
//        ┌────────┐  failure count >= threshold   ┌──────┐
//        │ Closed │──────────────────────────────▶│ Open │
//        └────┬───┘                               └──┬───┘
//             │                                      │
//             │◀──── success ─────┐                  │ break timer elapsed
//             │                   │                  │
//             │              ┌────┴──────┐◀──────────┘
//             │              │ HalfOpen  │
//             │              └────┬──────┘
//             │                   │ failure
//             │                   │
//             │              ┌────▼──────┐
//             │              │   Open    │ (reset break timer)
//             │              └───────────┘
//             │
//             │◀── success resets failure count ──┘
//
// The circuit breaker prevents cascading failures by short-circuiting calls
// to unhealthy dependencies. Based on Michael Nygard's Release It! (2007)
// stability pattern, later formalized by Polly and resilience4j.
//
// Key properties:
// - Closed: Normal operation. Tracks consecutive failures.
// - Open: Calls are rejected immediately. A break timer runs.
// - HalfOpen: Allows a single probe request. Success → Closed, Failure → Open.
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Picea.Mariana.CircuitBreaker;

// =============================================================================
// State
// =============================================================================

/// <summary>
/// The state of a circuit breaker automaton.
/// </summary>
public interface CircuitBreakerState
{
    /// <summary>Circuit is closed — normal operation. Tracks consecutive failures.</summary>
    record Closed(int ConsecutiveFailures, int FailureThreshold, TimeSpan BreakDuration) : CircuitBreakerState;

    /// <summary>Circuit is open — calls are rejected. Break timer is running.</summary>
    record Open(int FailureThreshold, TimeSpan BreakDuration, DateTimeOffset OpenedAt) : CircuitBreakerState;

    /// <summary>Circuit is half-open — a single probe request is allowed.</summary>
    record HalfOpen(int FailureThreshold, TimeSpan BreakDuration) : CircuitBreakerState;
}

// =============================================================================
// Events
// =============================================================================

/// <summary>
/// Events that drive the circuit breaker state machine.
/// </summary>
public interface CircuitBreakerEvent
{
    /// <summary>A call succeeded.</summary>
    record struct CallSucceeded : CircuitBreakerEvent;

    /// <summary>A call failed.</summary>
    record struct CallFailed(Exception Exception) : CircuitBreakerEvent;

    /// <summary>The break timer has elapsed — transition to half-open.</summary>
    record struct BreakTimerElapsed : CircuitBreakerEvent;

    /// <summary>A call was attempted while the circuit is open.</summary>
    record struct CallAttempted : CircuitBreakerEvent;
}

// =============================================================================
// Effects
// =============================================================================

/// <summary>
/// Effects produced by the circuit breaker automaton.
/// </summary>
public interface CircuitBreakerEffect
{
    /// <summary>No action needed.</summary>
    record struct None : CircuitBreakerEffect;

    /// <summary>Allow the call to proceed.</summary>
    record struct AllowCall : CircuitBreakerEffect;

    /// <summary>Reject the call — circuit is open.</summary>
    record struct RejectCall(TimeSpan RemainingBreakDuration) : CircuitBreakerEffect;

    /// <summary>Allow a single probe call (half-open).</summary>
    record struct AllowProbe : CircuitBreakerEffect;

    /// <summary>Trip the circuit — transition to open.</summary>
    record struct TripCircuit(TimeSpan BreakDuration) : CircuitBreakerEffect;

    /// <summary>Reset the circuit — transition back to closed.</summary>
    record struct ResetCircuit : CircuitBreakerEffect;
}

// =============================================================================
// Options
// =============================================================================

/// <summary>
/// Configuration for the circuit breaker strategy.
/// </summary>
/// <param name="FailureThreshold">Number of consecutive failures before tripping. Defaults to 5.</param>
/// <param name="BreakDuration">Duration the circuit stays open. Defaults to 30 seconds.</param>
/// <param name="ShouldHandle">Predicate for exceptions that count as failures. When null, all exceptions count.</param>
public record CircuitBreakerOptions(
    int FailureThreshold = 5,
    TimeSpan? BreakDuration = null,
    Func<Exception, bool>? ShouldHandle = null)
{
    /// <summary>The effective break duration (defaults to 30 seconds).</summary>
    public TimeSpan EffectiveBreakDuration => BreakDuration ?? TimeSpan.FromSeconds(30);
}

// =============================================================================
// Automaton — pure state machine
// =============================================================================

/// <summary>
/// A circuit breaker modeled as a Mealy machine automaton.
/// </summary>
public class CircuitBreakerAutomaton : Automaton<CircuitBreakerState, CircuitBreakerEvent, CircuitBreakerEffect, CircuitBreakerOptions>
{
    /// <summary>
    /// Initializes the circuit breaker in the Closed state with zero failures.
    /// </summary>
    public static (CircuitBreakerState State, CircuitBreakerEffect Effect) Initialize(CircuitBreakerOptions parameters) =>
        (new CircuitBreakerState.Closed(0, parameters.FailureThreshold, parameters.EffectiveBreakDuration),
         new CircuitBreakerEffect.AllowCall());

    /// <summary>
    /// Pure transition function for the circuit breaker.
    /// </summary>
    public static (CircuitBreakerState State, CircuitBreakerEffect Effect) Transition(
        CircuitBreakerState state, CircuitBreakerEvent @event) =>
        (state, @event) switch
        {
            // ── Closed state ──

            // Success resets the failure counter
            (CircuitBreakerState.Closed s, CircuitBreakerEvent.CallSucceeded) =>
                (new CircuitBreakerState.Closed(0, s.FailureThreshold, s.BreakDuration),
                 new CircuitBreakerEffect.None()),

            // Failure increments counter — trip if threshold reached
            (CircuitBreakerState.Closed s, CircuitBreakerEvent.CallFailed) when s.ConsecutiveFailures + 1 >= s.FailureThreshold =>
                (new CircuitBreakerState.Open(s.FailureThreshold, s.BreakDuration, DateTimeOffset.UtcNow),
                 new CircuitBreakerEffect.TripCircuit(s.BreakDuration)),

            // Failure below threshold — increment and continue
            (CircuitBreakerState.Closed s, CircuitBreakerEvent.CallFailed) =>
                (new CircuitBreakerState.Closed(s.ConsecutiveFailures + 1, s.FailureThreshold, s.BreakDuration),
                 new CircuitBreakerEffect.None()),

            // ── Open state ──

            // Call attempted while open — reject
            (CircuitBreakerState.Open s, CircuitBreakerEvent.CallAttempted) =>
                (s, new CircuitBreakerEffect.RejectCall(s.BreakDuration)),

            // Break timer elapsed — transition to half-open
            (CircuitBreakerState.Open s, CircuitBreakerEvent.BreakTimerElapsed) =>
                (new CircuitBreakerState.HalfOpen(s.FailureThreshold, s.BreakDuration),
                 new CircuitBreakerEffect.AllowProbe()),

            // ── HalfOpen state ──

            // Probe succeeded — reset to closed
            (CircuitBreakerState.HalfOpen s, CircuitBreakerEvent.CallSucceeded) =>
                (new CircuitBreakerState.Closed(0, s.FailureThreshold, s.BreakDuration),
                 new CircuitBreakerEffect.ResetCircuit()),

            // Probe failed — back to open
            (CircuitBreakerState.HalfOpen s, CircuitBreakerEvent.CallFailed) =>
                (new CircuitBreakerState.Open(s.FailureThreshold, s.BreakDuration, DateTimeOffset.UtcNow),
                 new CircuitBreakerEffect.TripCircuit(s.BreakDuration)),

            // Reject additional calls while half-open (only one probe allowed)
            (CircuitBreakerState.HalfOpen s, CircuitBreakerEvent.CallAttempted) =>
                (s, new CircuitBreakerEffect.RejectCall(TimeSpan.Zero)),

            // Default — absorb unexpected events
            _ => (state, new CircuitBreakerEffect.None())
        };
}

// =============================================================================
// Ergonomic API — CircuitBreaker.Execute()
// =============================================================================

/// <summary>
/// A stateful circuit breaker instance that tracks failure state across calls.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="Retry.Retry"/> and <see cref="Timeout.Timeout"/>, the circuit
/// breaker is inherently stateful — it must remember failure counts and open/closed
/// state across invocations. Create one instance per protected dependency.
/// </para>
/// <para>
/// Thread-safe: all state mutations are serialized via <see cref="SemaphoreSlim"/>.
/// </para>
/// </remarks>
public sealed class CircuitBreaker : IDisposable
{
    private readonly CircuitBreakerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CircuitBreakerState _state;
    private DateTimeOffset _openedAt;

    /// <summary>
    /// The current state of the circuit breaker.
    /// </summary>
    public CircuitBreakerState State => _state;

    /// <summary>
    /// Creates a new circuit breaker with the specified options.
    /// </summary>
    public CircuitBreaker(CircuitBreakerOptions? options = null)
    {
        _options = options ?? new CircuitBreakerOptions();
        _state = new CircuitBreakerState.Closed(0, _options.FailureThreshold, _options.EffectiveBreakDuration);
    }

    /// <summary>
    /// Executes an operation through the circuit breaker.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="cancellationToken">Token for cancellation.</param>
    /// <returns>
    /// <c>Ok(T)</c> if the operation succeeds, or
    /// <c>Err(ResilienceError)</c> with <see cref="FailureReason.CircuitOpen"/>
    /// if the circuit is open.
    /// </returns>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<Result<T, ResilienceError>> Execute<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        using var activity = ResilienceDiagnostics.Source.StartActivity("CircuitBreaker.Execute");

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "The operation was cancelled while waiting to enter the circuit breaker.",
                "CircuitBreaker",
                FailureReason.Cancelled));
        }

        try
        {
            // Check if circuit is open
            switch (_state)
            {
                case CircuitBreakerState.Open open:
                    var elapsed = DateTimeOffset.UtcNow - _openedAt;
                    if (elapsed >= _options.EffectiveBreakDuration)
                    {
                        // Transition to half-open — allow probe
                        _state = new CircuitBreakerState.HalfOpen(open.FailureThreshold, open.BreakDuration);
                        activity?.SetTag("circuit_breaker.state", "half_open");
                    }
                    else
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, "Circuit open");
                        activity?.SetTag("circuit_breaker.state", "open");

                        return Result<T, ResilienceError>.Err(new ResilienceError(
                            $"Circuit is open. Remaining break: {(_options.EffectiveBreakDuration - elapsed).TotalMilliseconds}ms.",
                            "CircuitBreaker",
                            FailureReason.CircuitOpen));
                    }

                    break;

                case CircuitBreakerState.HalfOpen:
                    // Already half-open — reject additional calls (only one probe)
                    activity?.SetStatus(ActivityStatusCode.Error, "Circuit half-open, probe in progress");

                    return Result<T, ResilienceError>.Err(new ResilienceError(
                        "Circuit is half-open. A probe request is already in progress.",
                        "CircuitBreaker",
                        FailureReason.CircuitOpen));
            }
        }
        finally
        {
            _gate.Release();
        }

        // Execute the operation outside the lock
        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _state = new CircuitBreakerState.Closed(0, _options.FailureThreshold, _options.EffectiveBreakDuration);
            }
            finally
            {
                _gate.Release();
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag("circuit_breaker.state", "closed");

            return Result<T, ResilienceError>.Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "Operation was cancelled.",
                "CircuitBreaker",
                FailureReason.Cancelled));
        }
        catch (Exception ex)
        {
            // Check if this exception should trip the breaker
            if (_options.ShouldHandle is not null && !_options.ShouldHandle(ex))
            {
                // Don't count this as a circuit breaker failure — it's a non-handled exception
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                return Result<T, ResilienceError>.Err(new ResilienceError(
                    $"Operation failed (not tracked by circuit breaker): {ex.Message}",
                    "CircuitBreaker",
                    FailureReason.Unknown,
                    ex));
            }

            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _state = _state switch
                {
                    CircuitBreakerState.Closed closed when closed.ConsecutiveFailures + 1 >= _options.FailureThreshold =>
                        Trip(),

                    CircuitBreakerState.Closed closed =>
                        new CircuitBreakerState.Closed(closed.ConsecutiveFailures + 1, _options.FailureThreshold, _options.EffectiveBreakDuration),

                    CircuitBreakerState.HalfOpen =>
                        Trip(),

                    _ => _state
                };
            }
            finally
            {
                _gate.Release();
            }

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("circuit_breaker.state", _state.GetType().Name.ToLowerInvariant());

            return Result<T, ResilienceError>.Err(new ResilienceError(
                $"Operation failed: {ex.Message}",
                "CircuitBreaker",
                FailureReason.Unknown,
                ex));
        }
    }

    /// <summary>
    /// Manually resets the circuit breaker to the Closed state.
    /// </summary>
    public async ValueTask Reset(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state = new CircuitBreakerState.Closed(0, _options.FailureThreshold, _options.EffectiveBreakDuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private CircuitBreakerState Trip()
    {
        _openedAt = DateTimeOffset.UtcNow;

        return new CircuitBreakerState.Open(
            _options.FailureThreshold,
            _options.EffectiveBreakDuration,
            _openedAt);
    }
}
