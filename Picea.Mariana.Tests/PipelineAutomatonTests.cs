using Picea.Mariana.Pipeline;

namespace Picea.Mariana.Tests;

/// <summary>
/// Tests for the <see cref="PipelineAutomaton{T}"/> Mealy machine — the state
/// machine that orchestrates pipeline execution via <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect,TParameters}"/>.
/// </summary>
public class PipelineAutomatonTests
{
    [Fact]
    public void Initialize_returns_pending_state_with_run_pipeline_effect()
    {
        ResilienceStrategy<int> strategy = (op, ct) => throw new NotImplementedException();
        Func<CancellationToken, ValueTask<int>> operation = _ => ValueTask.FromResult(42);
        var parameters = new PipelineParameters<int>(strategy, operation);

        var (state, effect) = PipelineAutomaton<int>.Initialize(parameters);

        Assert.IsType<PipelineState<int>.Pending>(state);
        var runPipeline = Assert.IsType<PipelineEffect<int>.RunPipeline>(effect);
        Assert.Same(strategy, runPipeline.Strategy);
        Assert.Same(operation, runPipeline.Operation);
    }

    [Fact]
    public void Transition_pending_plus_execute_produces_executing_state()
    {
        var state = new PipelineState<int>.Pending();
        var @event = new PipelineEvent<int>.Execute();

        var (newState, effect) = PipelineAutomaton<int>.Transition(state, @event);

        Assert.IsType<PipelineState<int>.Executing>(newState);
        Assert.IsType<PipelineEffect<int>.None>(effect);
    }

    [Fact]
    public void Transition_executing_plus_operation_completed_produces_succeeded()
    {
        var state = new PipelineState<int>.Executing();
        var @event = new PipelineEvent<int>.OperationCompleted(42);

        var (newState, effect) = PipelineAutomaton<int>.Transition(state, @event);

        var succeeded = Assert.IsType<PipelineState<int>.Succeeded>(newState);
        Assert.Equal(42, succeeded.Value);
        var reportSuccess = Assert.IsType<PipelineEffect<int>.ReportSuccess>(effect);
        Assert.Equal(42, reportSuccess.Value);
    }

    [Fact]
    public void Transition_executing_plus_operation_failed_produces_failed()
    {
        var state = new PipelineState<int>.Executing();
        var error = new ResilienceError("boom", "Test", FailureReason.RetriesExhausted);
        var @event = new PipelineEvent<int>.OperationFailed(error);

        var (newState, effect) = PipelineAutomaton<int>.Transition(state, @event);

        var failed = Assert.IsType<PipelineState<int>.Failed>(newState);
        Assert.Equal(error, failed.Error);
        var reportFailure = Assert.IsType<PipelineEffect<int>.ReportFailure>(effect);
        Assert.Equal(error, reportFailure.Error);
    }

    [Fact]
    public void Transition_succeeded_ignores_further_events()
    {
        var state = new PipelineState<int>.Succeeded(42);
        var @event = new PipelineEvent<int>.OperationFailed(
            new ResilienceError("late", "Test", FailureReason.Unknown));

        var (newState, effect) = PipelineAutomaton<int>.Transition(state, @event);

        var succeeded = Assert.IsType<PipelineState<int>.Succeeded>(newState);
        Assert.Equal(42, succeeded.Value);
        Assert.IsType<PipelineEffect<int>.None>(effect);
    }

    [Fact]
    public void Transition_failed_ignores_further_events()
    {
        var error = new ResilienceError("permanent", "Test", FailureReason.CircuitOpen);
        var state = new PipelineState<int>.Failed(error);
        var @event = new PipelineEvent<int>.OperationCompleted(99);

        var (newState, effect) = PipelineAutomaton<int>.Transition(state, @event);

        var failed = Assert.IsType<PipelineState<int>.Failed>(newState);
        Assert.Equal(error, failed.Error);
        Assert.IsType<PipelineEffect<int>.None>(effect);
    }

    [Fact]
    public async Task Execute_runs_through_automaton_runtime_on_success()
    {
        var result = await ResilienceStrategy.Identity<int>()
            .Execute(_ => ValueTask.FromResult(42));

        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task Execute_runs_through_automaton_runtime_on_failure()
    {
        var result = await ResilienceStrategy.Identity<int>()
            .Execute(_ => throw new InvalidOperationException("boom"));

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Unknown, result.Error.Reason);
        Assert.Contains("boom", result.Error.Message);
    }

    [Fact]
    public async Task Pipeline_automaton_orchestrates_composed_strategies()
    {
        var callCount = 0;

        var pipeline = ResilienceStrategy
            .WithRetry<string>(new Retry.RetryOptions(MaxAttempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1)))
            .Then(ResilienceStrategy.WithTimeout<string>(new Timeout.TimeoutOptions(TimeSpan.FromSeconds(5))));

        var result = await pipeline.Execute(async ct =>
        {
            callCount++;
            if (callCount < 2)
                throw new InvalidOperationException("transient");
            return "recovered";
        });

        Assert.True(result.IsOk);
        Assert.Equal("recovered", result.Value);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Pipeline_automaton_handles_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await ResilienceStrategy.Identity<int>()
            .Execute(
                async ct =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    return 42;
                },
                cts.Token);

        Assert.True(result.IsErr);
        Assert.Equal(FailureReason.Cancelled, result.Error.Reason);
    }

    [Fact]
    public async Task Pipeline_automaton_handles_bridge_exception_from_then()
    {
        var pipeline = ResilienceStrategy
            .WithRetry<int>(new Retry.RetryOptions(MaxAttempts: 1, BaseDelay: TimeSpan.FromMilliseconds(1)))
            .Then(ResilienceStrategy.WithTimeout<int>(new Timeout.TimeoutOptions(TimeSpan.FromMilliseconds(50))));

        var result = await pipeline.Execute(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return 42;
        });

        Assert.True(result.IsErr);
    }

    [Fact]
    public void Pending_plus_operation_completed_goes_directly_to_succeeded()
    {
        // OperationCompleted can arrive from any non-terminal state
        // (Initialize emits RunPipeline immediately, so the interpreter
        //  might fire before the Execute event is processed)
        var state = new PipelineState<int>.Pending();
        var @event = new PipelineEvent<int>.OperationCompleted(42);

        var (newState, _) = PipelineAutomaton<int>.Transition(state, @event);

        var succeeded = Assert.IsType<PipelineState<int>.Succeeded>(newState);
        Assert.Equal(42, succeeded.Value);
    }
}
