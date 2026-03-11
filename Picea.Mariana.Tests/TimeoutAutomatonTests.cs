using Picea.Mariana.Timeout;

namespace Picea.Mariana.Tests;

public class TimeoutAutomatonTests
{
    // =========================================================================
    // Initialize
    // =========================================================================

    [Fact]
    public void Initialize_produces_running_state_and_start_timer_effect()
    {
        var (state, effect) = TimeoutAutomaton.Initialize(new TimeoutOptions(Duration: TimeSpan.FromSeconds(5)));

        var running = Assert.IsType<TimeoutState.Running>(state);
        Assert.Equal(TimeSpan.FromSeconds(5), running.Duration);

        var start = Assert.IsType<TimeoutEffect.StartTimer>(effect);
        Assert.Equal(TimeSpan.FromSeconds(5), start.Duration);
    }

    // =========================================================================
    // Transitions — success
    // =========================================================================

    [Fact]
    public void Transition_running_completed_goes_to_completed()
    {
        var state = new TimeoutState.Running(TimeSpan.FromSeconds(5));
        var (newState, effect) = TimeoutAutomaton.Transition(state, new TimeoutEvent.OperationCompleted());

        Assert.IsType<TimeoutState.Completed>(newState);
        Assert.IsType<TimeoutEffect.ReportCompleted>(effect);
    }

    // =========================================================================
    // Transitions — timeout
    // =========================================================================

    [Fact]
    public void Transition_running_deadline_exceeded_goes_to_timed_out()
    {
        var state = new TimeoutState.Running(TimeSpan.FromSeconds(5));
        var (newState, effect) = TimeoutAutomaton.Transition(state, new TimeoutEvent.DeadlineExceeded());

        var timedOut = Assert.IsType<TimeoutState.TimedOut>(newState);
        Assert.Equal(TimeSpan.FromSeconds(5), timedOut.Duration);

        var report = Assert.IsType<TimeoutEffect.ReportTimedOut>(effect);
        Assert.Equal(TimeSpan.FromSeconds(5), report.Duration);
    }

    // =========================================================================
    // Terminal states are absorbing
    // =========================================================================

    [Fact]
    public void Completed_absorbs_all_events()
    {
        var state = new TimeoutState.Completed(TimeSpan.FromSeconds(5));

        var (s1, e1) = TimeoutAutomaton.Transition(state, new TimeoutEvent.OperationCompleted());
        Assert.IsType<TimeoutState.Completed>(s1);
        Assert.IsType<TimeoutEffect.None>(e1);

        var (s2, e2) = TimeoutAutomaton.Transition(state, new TimeoutEvent.DeadlineExceeded());
        Assert.IsType<TimeoutState.Completed>(s2);
        Assert.IsType<TimeoutEffect.None>(e2);
    }

    [Fact]
    public void TimedOut_absorbs_all_events()
    {
        var state = new TimeoutState.TimedOut(TimeSpan.FromSeconds(5));

        var (s1, e1) = TimeoutAutomaton.Transition(state, new TimeoutEvent.OperationCompleted());
        Assert.IsType<TimeoutState.TimedOut>(s1);
        Assert.IsType<TimeoutEffect.None>(e1);
    }

    // =========================================================================
    // Default options
    // =========================================================================

    [Fact]
    public void Default_duration_is_30_seconds()
    {
        var opts = new TimeoutOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), opts.EffectiveDuration);
    }
}
