using Picea.Mariana.CircuitBreaker;

namespace Picea.Mariana.Tests;

public class CircuitBreakerAutomatonTests
{
    // =========================================================================
    // Initialize
    // =========================================================================

    [Fact]
    public void Initialize_produces_closed_state_with_zero_failures()
    {
        var (state, effect) = CircuitBreakerAutomaton.Initialize(new CircuitBreakerOptions(FailureThreshold: 3));

        var closed = Assert.IsType<CircuitBreakerState.Closed>(state);
        Assert.Equal(0, closed.ConsecutiveFailures);
        Assert.Equal(3, closed.FailureThreshold);

        Assert.IsType<CircuitBreakerEffect.AllowCall>(effect);
    }

    // =========================================================================
    // Closed — success resets counter
    // =========================================================================

    [Fact]
    public void Closed_success_resets_failure_counter()
    {
        var state = new CircuitBreakerState.Closed(2, 5, TimeSpan.FromSeconds(30));
        var (newState, _) = CircuitBreakerAutomaton.Transition(state, new CircuitBreakerEvent.CallSucceeded());

        var closed = Assert.IsType<CircuitBreakerState.Closed>(newState);
        Assert.Equal(0, closed.ConsecutiveFailures);
    }

    // =========================================================================
    // Closed — failure increments counter
    // =========================================================================

    [Fact]
    public void Closed_failure_increments_counter()
    {
        var state = new CircuitBreakerState.Closed(1, 5, TimeSpan.FromSeconds(30));
        var (newState, effect) = CircuitBreakerAutomaton.Transition(
            state, new CircuitBreakerEvent.CallFailed(new Exception("fail")));

        var closed = Assert.IsType<CircuitBreakerState.Closed>(newState);
        Assert.Equal(2, closed.ConsecutiveFailures);
        Assert.IsType<CircuitBreakerEffect.None>(effect);
    }

    // =========================================================================
    // Closed — failure at threshold trips circuit
    // =========================================================================

    [Fact]
    public void Closed_failure_at_threshold_trips_to_open()
    {
        var state = new CircuitBreakerState.Closed(2, 3, TimeSpan.FromSeconds(30));
        var (newState, effect) = CircuitBreakerAutomaton.Transition(
            state, new CircuitBreakerEvent.CallFailed(new Exception("fail")));

        Assert.IsType<CircuitBreakerState.Open>(newState);
        Assert.IsType<CircuitBreakerEffect.TripCircuit>(effect);
    }

    // =========================================================================
    // Open — rejects calls
    // =========================================================================

    [Fact]
    public void Open_rejects_call_attempts()
    {
        var state = new CircuitBreakerState.Open(3, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
        var (newState, effect) = CircuitBreakerAutomaton.Transition(
            state, new CircuitBreakerEvent.CallAttempted());

        Assert.IsType<CircuitBreakerState.Open>(newState);
        var reject = Assert.IsType<CircuitBreakerEffect.RejectCall>(effect);
        Assert.Equal(TimeSpan.FromSeconds(30), reject.RemainingBreakDuration);
    }

    // =========================================================================
    // Open — break timer → half-open
    // =========================================================================

    [Fact]
    public void Open_break_timer_elapsed_transitions_to_half_open()
    {
        var state = new CircuitBreakerState.Open(3, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
        var (newState, effect) = CircuitBreakerAutomaton.Transition(
            state, new CircuitBreakerEvent.BreakTimerElapsed());

        Assert.IsType<CircuitBreakerState.HalfOpen>(newState);
        Assert.IsType<CircuitBreakerEffect.AllowProbe>(effect);
    }

    // =========================================================================
    // HalfOpen — probe success → closed
    // =========================================================================

    [Fact]
    public void HalfOpen_success_resets_to_closed()
    {
        var state = new CircuitBreakerState.HalfOpen(3, TimeSpan.FromSeconds(30));
        var (newState, effect) = CircuitBreakerAutomaton.Transition(
            state, new CircuitBreakerEvent.CallSucceeded());

        var closed = Assert.IsType<CircuitBreakerState.Closed>(newState);
        Assert.Equal(0, closed.ConsecutiveFailures);
        Assert.IsType<CircuitBreakerEffect.ResetCircuit>(effect);
    }

    // =========================================================================
    // HalfOpen — probe failure → open
    // =========================================================================

    [Fact]
    public void HalfOpen_failure_trips_back_to_open()
    {
        var state = new CircuitBreakerState.HalfOpen(3, TimeSpan.FromSeconds(30));
        var (newState, effect) = CircuitBreakerAutomaton.Transition(
            state, new CircuitBreakerEvent.CallFailed(new Exception("probe fail")));

        Assert.IsType<CircuitBreakerState.Open>(newState);
        Assert.IsType<CircuitBreakerEffect.TripCircuit>(effect);
    }

    // =========================================================================
    // HalfOpen — rejects additional calls
    // =========================================================================

    [Fact]
    public void HalfOpen_rejects_additional_call_attempts()
    {
        var state = new CircuitBreakerState.HalfOpen(3, TimeSpan.FromSeconds(30));
        var (_, effect) = CircuitBreakerAutomaton.Transition(
            state, new CircuitBreakerEvent.CallAttempted());

        Assert.IsType<CircuitBreakerEffect.RejectCall>(effect);
    }

    // =========================================================================
    // Full lifecycle
    // =========================================================================

    [Fact]
    public void Full_lifecycle_closed_to_open_to_half_open_to_closed()
    {
        // Start closed
        var (state, _) = CircuitBreakerAutomaton.Initialize(new CircuitBreakerOptions(FailureThreshold: 2));
        Assert.IsType<CircuitBreakerState.Closed>(state);

        // Failure 1
        (state, _) = CircuitBreakerAutomaton.Transition(state, new CircuitBreakerEvent.CallFailed(new Exception()));
        var closed = Assert.IsType<CircuitBreakerState.Closed>(state);
        Assert.Equal(1, closed.ConsecutiveFailures);

        // Failure 2 — trips
        (state, var effect) = CircuitBreakerAutomaton.Transition(state, new CircuitBreakerEvent.CallFailed(new Exception()));
        Assert.IsType<CircuitBreakerState.Open>(state);
        Assert.IsType<CircuitBreakerEffect.TripCircuit>(effect);

        // Break timer elapses
        (state, effect) = CircuitBreakerAutomaton.Transition(state, new CircuitBreakerEvent.BreakTimerElapsed());
        Assert.IsType<CircuitBreakerState.HalfOpen>(state);
        Assert.IsType<CircuitBreakerEffect.AllowProbe>(effect);

        // Probe succeeds
        (state, effect) = CircuitBreakerAutomaton.Transition(state, new CircuitBreakerEvent.CallSucceeded());
        Assert.IsType<CircuitBreakerState.Closed>(state);
        Assert.IsType<CircuitBreakerEffect.ResetCircuit>(effect);
    }
}
