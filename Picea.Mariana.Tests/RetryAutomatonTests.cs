using Picea.Mariana.Retry;

namespace Picea.Mariana.Tests;

public class RetryAutomatonTests
{
    // =========================================================================
    // Initialize
    // =========================================================================

    [Fact]
    public void Initialize_produces_waiting_state_and_execute_effect()
    {
        var (state, effect) = RetryAutomaton.Initialize(new RetryOptions(MaxAttempts: 3));

        var waiting = Assert.IsType<RetryState.Waiting>(state);
        Assert.Equal(1, waiting.Attempt);
        Assert.Equal(3, waiting.MaxAttempts);

        var execute = Assert.IsType<RetryEffect.ExecuteAttempt>(effect);
        Assert.Equal(1, execute.Attempt);
    }

    // =========================================================================
    // Transitions — success
    // =========================================================================

    [Fact]
    public void Transition_waiting_succeeded_goes_to_succeeded()
    {
        var state = new RetryState.Waiting(1, 3);
        var (newState, effect) = RetryAutomaton.Transition(state, new RetryEvent.AttemptSucceeded());

        var succeeded = Assert.IsType<RetryState.Succeeded>(newState);
        Assert.Equal(1, succeeded.Attempt);

        var report = Assert.IsType<RetryEffect.ReportSuccess>(effect);
        Assert.Equal(1, report.TotalAttempts);
    }

    [Fact]
    public void Transition_waiting_succeeded_after_retries()
    {
        var state = new RetryState.Waiting(3, 5);
        var (newState, _) = RetryAutomaton.Transition(state, new RetryEvent.AttemptSucceeded());

        var succeeded = Assert.IsType<RetryState.Succeeded>(newState);
        Assert.Equal(3, succeeded.Attempt);
    }

    // =========================================================================
    // Transitions — failure with retries remaining
    // =========================================================================

    [Fact]
    public void Transition_waiting_failed_with_retries_goes_to_delaying()
    {
        var state = new RetryState.Waiting(1, 3);
        var exception = new TimeoutException("timeout");
        var (newState, effect) = RetryAutomaton.Transition(state, new RetryEvent.AttemptFailed(exception));

        Assert.IsType<RetryState.Delaying>(newState);
        var schedule = Assert.IsType<RetryEffect.ScheduleRetry>(effect);
        Assert.Equal(2, schedule.NextAttempt);
    }

    // =========================================================================
    // Transitions — failure without retries
    // =========================================================================

    [Fact]
    public void Transition_waiting_failed_at_max_goes_to_exhausted()
    {
        var state = new RetryState.Waiting(3, 3);
        var exception = new InvalidOperationException("fail");
        var (newState, effect) = RetryAutomaton.Transition(state, new RetryEvent.AttemptFailed(exception));

        var exhausted = Assert.IsType<RetryState.Exhausted>(newState);
        Assert.Equal(3, exhausted.Attempt);
        Assert.Same(exception, exhausted.LastException);

        var report = Assert.IsType<RetryEffect.ReportExhausted>(effect);
        Assert.Equal(3, report.TotalAttempts);
        Assert.Same(exception, report.LastException);
    }

    // =========================================================================
    // Transitions — delay elapsed
    // =========================================================================

    [Fact]
    public void Transition_delaying_delay_elapsed_goes_to_waiting()
    {
        var state = new RetryState.Delaying(1, 3, TimeSpan.FromSeconds(1));
        var (newState, effect) = RetryAutomaton.Transition(state, new RetryEvent.DelayElapsed());

        var waiting = Assert.IsType<RetryState.Waiting>(newState);
        Assert.Equal(2, waiting.Attempt);

        var execute = Assert.IsType<RetryEffect.ExecuteAttempt>(effect);
        Assert.Equal(2, execute.Attempt);
    }

    // =========================================================================
    // Terminal states are absorbing
    // =========================================================================

    [Fact]
    public void Transition_succeeded_absorbs_all_events()
    {
        var state = new RetryState.Succeeded(2, 3);

        var (s1, e1) = RetryAutomaton.Transition(state, new RetryEvent.AttemptSucceeded());
        Assert.IsType<RetryState.Succeeded>(s1);
        Assert.IsType<RetryEffect.None>(e1);

        var (s2, e2) = RetryAutomaton.Transition(state, new RetryEvent.AttemptFailed(new Exception()));
        Assert.IsType<RetryState.Succeeded>(s2);
        Assert.IsType<RetryEffect.None>(e2);

        var (s3, e3) = RetryAutomaton.Transition(state, new RetryEvent.DelayElapsed());
        Assert.IsType<RetryState.Succeeded>(s3);
        Assert.IsType<RetryEffect.None>(e3);
    }

    [Fact]
    public void Transition_exhausted_absorbs_all_events()
    {
        var state = new RetryState.Exhausted(3, 3, new Exception("last"));

        var (s1, e1) = RetryAutomaton.Transition(state, new RetryEvent.AttemptSucceeded());
        Assert.IsType<RetryState.Exhausted>(s1);
        Assert.IsType<RetryEffect.None>(e1);

        var (s2, e2) = RetryAutomaton.Transition(state, new RetryEvent.AttemptFailed(new Exception()));
        Assert.IsType<RetryState.Exhausted>(s2);
        Assert.IsType<RetryEffect.None>(e2);
    }

    // =========================================================================
    // Full lifecycle — drive the automaton manually
    // =========================================================================

    [Fact]
    public void Full_lifecycle_success_on_second_attempt()
    {
        // Initialize
        var (state, effect) = RetryAutomaton.Initialize(new RetryOptions(MaxAttempts: 3));
        Assert.IsType<RetryState.Waiting>(state);
        Assert.IsType<RetryEffect.ExecuteAttempt>(effect);

        // Attempt 1 fails
        (state, effect) = RetryAutomaton.Transition(state, new RetryEvent.AttemptFailed(new Exception("fail 1")));
        Assert.IsType<RetryState.Delaying>(state);
        Assert.IsType<RetryEffect.ScheduleRetry>(effect);

        // Delay elapses
        (state, effect) = RetryAutomaton.Transition(state, new RetryEvent.DelayElapsed());
        Assert.IsType<RetryState.Waiting>(state);
        var execute = Assert.IsType<RetryEffect.ExecuteAttempt>(effect);
        Assert.Equal(2, execute.Attempt);

        // Attempt 2 succeeds
        (state, effect) = RetryAutomaton.Transition(state, new RetryEvent.AttemptSucceeded());
        Assert.IsType<RetryState.Succeeded>(state);
        var report = Assert.IsType<RetryEffect.ReportSuccess>(effect);
        Assert.Equal(2, report.TotalAttempts);
    }

    [Fact]
    public void Full_lifecycle_all_attempts_exhausted()
    {
        var opts = new RetryOptions(MaxAttempts: 2);
        var (state, _) = RetryAutomaton.Initialize(opts);

        // Attempt 1 fails
        (state, _) = RetryAutomaton.Transition(state, new RetryEvent.AttemptFailed(new Exception("fail 1")));
        Assert.IsType<RetryState.Delaying>(state);

        // Delay elapses
        (state, _) = RetryAutomaton.Transition(state, new RetryEvent.DelayElapsed());
        Assert.IsType<RetryState.Waiting>(state);

        // Attempt 2 fails — exhausted
        var lastEx = new Exception("fail 2");
        (state, var effect) = RetryAutomaton.Transition(state, new RetryEvent.AttemptFailed(lastEx));
        var exhausted = Assert.IsType<RetryState.Exhausted>(state);
        Assert.Equal(2, exhausted.Attempt);
        Assert.Same(lastEx, exhausted.LastException);

        var report = Assert.IsType<RetryEffect.ReportExhausted>(effect);
        Assert.Same(lastEx, report.LastException);
    }
}
