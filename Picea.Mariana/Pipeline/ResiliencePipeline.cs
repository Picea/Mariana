// =============================================================================
// Resilience Pipeline — Delegate Composition via Kleisli Arrows
// =============================================================================
// Resilience strategies compose exactly like the core Automaton's Observer and
// Interpreter pipelines: they are delegates combined with standard FP
// combinators (Then, Where, Catch).
//
// Mathematically, a ResilienceStrategy<T> is a Kleisli arrow in the
// Result<T, ResilienceError> monad:
//
//     ResilienceStrategy<T> : (Func<CT, VT<T>>, CT) → VT<Result<T, ResilienceError>>
//
// Composition via Then forms a monoid:
//
//     (f · g)(op, ct) = f(ct' => g(op, ct'), ct)
//
// This is middleware composition — the outer strategy wraps the inner
// strategy's call to the operation, forming a Russian-doll nesting:
//
//     Retry(Timeout(CircuitBreaker(operation)))
//
// The identity element is the "passthrough" strategy that just executes
// the operation and wraps the result in Ok.
//
// This is the same structural pattern as Observer.Then() and
// Interpreter.Then() from the core Automaton runtime — delegate
// composition with short-circuit error propagation via Result.
//
// Pipeline execution itself is an automaton:
//
//     PipelineAutomaton<T> : Automaton<PipelineState<T>, PipelineEvent<T>, PipelineEffect<T>, ...>
//
// The Execute() terminal combinator creates an AutomatonRuntime internally
// — the same pattern as MvuRuntime.Start() and AggregateRunner.Handle().
// The pipeline automaton transitions through Pending → Executing → terminal
// (Succeeded or Failed), with effects interpreted by running the composed
// strategy delegate. This closes the loop: resilience strategies compose
// as delegates, but pipeline orchestration is a Mealy machine.
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Picea.Mariana.Retry;
using Picea.Mariana.Timeout;

namespace Picea.Mariana.Pipeline;

// =============================================================================
// Pipeline Automaton — Mealy machine for pipeline execution orchestration
// =============================================================================

/// <summary>
/// The state of a pipeline execution automaton.
/// </summary>
/// <remarks>
/// <para>
/// This is a discriminated union with four cases, modeling the lifecycle
/// of a single pipeline execution:
/// </para>
/// <list type="bullet">
///     <item><term>Pending</term><description>Initial state — not yet executing.</description></item>
///     <item><term>Executing</term><description>The composed strategy is running the operation.</description></item>
///     <item><term>Succeeded</term><description>Terminal — the operation completed successfully.</description></item>
///     <item><term>Failed</term><description>Terminal — the pipeline produced a resilience error.</description></item>
/// </list>
/// </remarks>
/// <typeparam name="T">The return type of the operation.</typeparam>
public interface PipelineState<T>
{
    /// <summary>
    /// Initial state — the pipeline has not yet started executing.
    /// Carries the strategy and operation so that the <see cref="PipelineEvent{T}.Execute"/>
    /// event can produce a <see cref="PipelineEffect{T}.RunPipeline"/> effect as a fallback
    /// when the initial effect from <c>Initialize</c> was not interpreted.
    /// </summary>
    /// <param name="Strategy">The composed resilience strategy delegate.</param>
    /// <param name="Operation">The user's operation to protect.</param>
    sealed record Pending(
        ResilienceStrategy<T>? Strategy = null,
        Func<CancellationToken, ValueTask<T>>? Operation = null) : PipelineState<T>;

    /// <summary>
    /// The composed strategy is running the operation.
    /// </summary>
    sealed record Executing : PipelineState<T>;

    /// <summary>
    /// Terminal state — the operation completed successfully with a value.
    /// </summary>
    /// <param name="Value">The successful result value.</param>
    sealed record Succeeded(T Value) : PipelineState<T>;

    /// <summary>
    /// Terminal state — the pipeline produced a resilience error.
    /// </summary>
    /// <param name="Error">The structured error from the resilience pipeline.</param>
    sealed record Failed(ResilienceError Error) : PipelineState<T>;
}

/// <summary>
/// The events that drive pipeline state transitions.
/// </summary>
/// <typeparam name="T">The return type of the operation.</typeparam>
public interface PipelineEvent<T>
{
    /// <summary>
    /// Command to begin executing the pipeline.
    /// </summary>
    sealed record Execute : PipelineEvent<T>;

    /// <summary>
    /// The composed strategy completed the operation successfully.
    /// </summary>
    /// <param name="Value">The successful result value.</param>
    sealed record OperationCompleted(T Value) : PipelineEvent<T>;

    /// <summary>
    /// The composed strategy reported a resilience error.
    /// </summary>
    /// <param name="Error">The structured error from the strategy pipeline.</param>
    sealed record OperationFailed(ResilienceError Error) : PipelineEvent<T>;
}

/// <summary>
/// The effects produced by pipeline state transitions.
/// </summary>
/// <remarks>
/// Effects are data — they describe what should happen, but the interpreter
/// executes them. This separation is the core Mealy machine principle:
/// the transition function is pure, side effects are external.
/// </remarks>
/// <typeparam name="T">The return type of the operation.</typeparam>
public interface PipelineEffect<T>
{
    /// <summary>
    /// Run the composed strategy with the given operation.
    /// The interpreter will execute the strategy and dispatch
    /// <see cref="PipelineEvent{T}.OperationCompleted"/> or
    /// <see cref="PipelineEvent{T}.OperationFailed"/> back into the automaton.
    /// </summary>
    /// <param name="Strategy">The composed resilience strategy delegate.</param>
    /// <param name="Operation">The user's operation to protect.</param>
    sealed record RunPipeline(
        ResilienceStrategy<T> Strategy,
        Func<CancellationToken, ValueTask<T>> Operation) : PipelineEffect<T>;

    /// <summary>
    /// Report that the pipeline completed successfully.
    /// No-op effect — the state already holds the value.
    /// </summary>
    sealed record ReportSuccess(T Value) : PipelineEffect<T>;

    /// <summary>
    /// Report that the pipeline failed.
    /// No-op effect — the state already holds the error.
    /// </summary>
    sealed record ReportFailure(ResilienceError Error) : PipelineEffect<T>;

    /// <summary>
    /// No effect to execute.
    /// </summary>
    sealed record None : PipelineEffect<T>;
}

/// <summary>
/// Initialization parameters for the pipeline automaton.
/// </summary>
/// <param name="Strategy">The composed resilience strategy delegate.</param>
/// <param name="Operation">The user's operation to protect.</param>
/// <typeparam name="T">The return type of the operation.</typeparam>
public sealed record PipelineParameters<T>(
    ResilienceStrategy<T> Strategy,
    Func<CancellationToken, ValueTask<T>> Operation);

/// <summary>
/// A Mealy machine that orchestrates the execution of a composed resilience
/// strategy pipeline via <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect,TParameters}"/>.
/// </summary>
/// <remarks>
/// <para>
/// This automaton has three transitions:
/// </para>
/// <list type="number">
///     <item><description><c>Pending × Execute → Executing + RunPipeline</c></description></item>
///     <item><description><c>Executing × OperationCompleted → Succeeded + ReportSuccess</c></description></item>
///     <item><description><c>Executing × OperationFailed → Failed + ReportFailure</c></description></item>
/// </list>
/// <para>
/// The interpreter receives <see cref="PipelineEffect{T}.RunPipeline"/> and runs the
/// composed <see cref="ResilienceStrategy{T}"/> delegate. The result is fed back
/// as an <see cref="PipelineEvent{T}.OperationCompleted"/> or
/// <see cref="PipelineEvent{T}.OperationFailed"/> event — exactly as
/// <c>MvuRuntime</c> feeds user actions back as domain events.
/// </para>
/// <para>
/// The <see cref="ResilienceStrategyExtensions.Execute{T}"/> method is the
/// specialized runtime for this automaton — it creates and starts an
/// <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect,TParameters}"/>,
/// allowing the <see cref="Initialize(PipelineParameters{T})"/> effect
/// <see cref="PipelineEffect{T}.RunPipeline"/> to be interpreted immediately and
/// the pipeline to begin executing without an explicit
/// <see cref="PipelineEvent{T}.Execute"/> dispatch. The <c>Execute</c> event
/// remains available as a fallback trigger if an interpreter chooses not to
/// interpret the initial effect.
/// </para>
/// </remarks>
/// <typeparam name="T">The return type of the operation.</typeparam>
public sealed class PipelineAutomaton<T>
    : Automaton<PipelineState<T>, PipelineEvent<T>, PipelineEffect<T>, PipelineParameters<T>>
{
    /// <summary>
    /// Initializes the pipeline in <see cref="PipelineState{T}.Pending"/> state
    /// and immediately emits a <see cref="PipelineEffect{T}.RunPipeline"/> effect
    /// so the interpreter starts execution without an extra dispatch.
    /// </summary>
    public static (PipelineState<T> State, PipelineEffect<T> Effect) Initialize(PipelineParameters<T> parameters) =>
        (new PipelineState<T>.Pending(parameters.Strategy, parameters.Operation),
         new PipelineEffect<T>.RunPipeline(parameters.Strategy, parameters.Operation));

    /// <summary>
    /// Pure transition function for the pipeline automaton.
    /// </summary>
    public static (PipelineState<T> State, PipelineEffect<T> Effect) Transition(
        PipelineState<T> state, PipelineEvent<T> @event) =>
        (state, @event) switch
        {
            // Terminal states ignore further events
            (PipelineState<T>.Succeeded or PipelineState<T>.Failed, _) =>
                (state, new PipelineEffect<T>.None()),

            // Pending → Execute triggers the pipeline (fallback if Initialize effect wasn't interpreted)
            (PipelineState<T>.Pending { Strategy: not null, Operation: not null } p, PipelineEvent<T>.Execute) =>
                (new PipelineState<T>.Executing(),
                 new PipelineEffect<T>.RunPipeline(p.Strategy, p.Operation)),

            (PipelineState<T>.Pending, PipelineEvent<T>.Execute) =>
                (new PipelineState<T>.Executing(), new PipelineEffect<T>.None()),

            // Operation completed — transition to terminal success
            (_, PipelineEvent<T>.OperationCompleted completed) =>
                (new PipelineState<T>.Succeeded(completed.Value),
                 new PipelineEffect<T>.ReportSuccess(completed.Value)),

            // Operation failed — transition to terminal failure
            (_, PipelineEvent<T>.OperationFailed failed) =>
                (new PipelineState<T>.Failed(failed.Error),
                 new PipelineEffect<T>.ReportFailure(failed.Error)),

            // Any other combination — no-op
            _ => (state, new PipelineEffect<T>.None())
        };
}

// =============================================================================
// Delegate — the single building block
// =============================================================================

/// <summary>
/// A resilience strategy is a delegate that wraps an operation with
/// cross-cutting resilience behavior (retry, timeout, circuit breaking, etc.).
/// </summary>
/// <remarks>
/// <para>
/// This delegate is the resilience analogue of <see cref="Observer{TState,TEvent,TEffect}"/>
/// and <see cref="Interpreter{TEffect,TEvent}"/> from the core runtime.
/// Like those delegates, strategies compose via combinators:
/// <see cref="ResilienceStrategyExtensions.Then{T}"/> (Kleisli composition),
/// <see cref="ResilienceStrategyExtensions.Where{T}"/> (guard/filter),
/// <see cref="ResilienceStrategyExtensions.Catch{T}"/> (error recovery).
/// </para>
/// <para>
/// A strategy receives the raw operation and a cancellation token, executes
/// the operation with whatever resilience logic it provides, and returns
/// <c>Result&lt;T, ResilienceError&gt;</c> — errors are values, not exceptions.
/// </para>
/// </remarks>
/// <typeparam name="T">The return type of the operation.</typeparam>
/// <param name="operation">The operation to execute.</param>
/// <param name="cancellationToken">Token for cooperative cancellation.</param>
/// <returns>
/// <c>Ok(T)</c> on success, <c>Err(ResilienceError)</c> on failure.
/// </returns>
/// <example>
/// <code>
/// // A simple logging strategy:
/// ResilienceStrategy&lt;int&gt; log = async (operation, ct) =&gt;
/// {
///     Console.WriteLine("Before");
///     var result = await operation(ct);
///     Console.WriteLine("After");
///     return Result&lt;int, ResilienceError&gt;.Ok(result);
/// };
///
/// // Compose with retry:
/// var pipeline = ResilienceStrategy.WithRetry&lt;int&gt;()
///     .Then(log);
///
/// var result = await pipeline.Execute(ct =&gt; httpClient.GetAsync(url, ct));
/// </code>
/// </example>
public delegate ValueTask<Result<T, ResilienceError>> ResilienceStrategy<T>(
    Func<CancellationToken, ValueTask<T>> operation,
    CancellationToken cancellationToken);

// =============================================================================
// Combinators — same vocabulary as Observer and Interpreter
// =============================================================================

/// <summary>
/// Combinators for composing resilience strategies into pipelines.
/// </summary>
/// <remarks>
/// <para>
/// These combinators mirror the core Automaton runtime's
/// <see cref="ObserverExtensions"/> and <see cref="InterpreterExtensions"/>:
/// </para>
/// <list type="table">
///     <listheader><term>Combinator</term><description>FP Concept</description></listheader>
///     <item><term><see cref="Then{T}"/></term><description>Kleisli composition — outer wraps inner, short-circuits on Err</description></item>
///     <item><term><see cref="Where{T}"/></term><description>Guard / filter — conditionally apply the strategy</description></item>
///     <item><term><see cref="Catch{T}"/></term><description>Error recovery — transform or swallow errors</description></item>
/// </list>
/// <para>
/// <see cref="Then{T}"/> performs middleware-style nesting: given strategies
/// <c>f</c> and <c>g</c>, <c>f.Then(g)</c> produces a strategy where
/// <c>f</c> wraps <c>g</c>'s execution of the operation:
/// </para>
/// <code>
///     f.Then(g)(op, ct) = f(ct' =&gt; g(op, ct').Unwrap(), ct)
/// </code>
/// <para>
/// This produces the execution order: f-before → g-before → operation → g-after → f-after,
/// matching the Russian-doll middleware pattern used by ASP.NET Core, Express.js,
/// and other pipeline architectures.
/// </para>
/// </remarks>
public static class ResilienceStrategyExtensions
{
    /// <summary>
    /// Composes two strategies: <paramref name="outer"/> wraps <paramref name="inner"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is Kleisli composition adapted for middleware nesting. The outer
    /// strategy controls the lifecycle (retry, timeout) while the inner strategy
    /// adds its own behavior (circuit breaking, rate limiting) closer to the
    /// operation.
    /// </para>
    /// <para>
    /// Execution order: outer-before → inner-before → operation → inner-after → outer-after.
    /// </para>
    /// <para>
    /// Uses async elision: when the inner strategy completes synchronously,
    /// no async state machine is allocated on the heap.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Retry wraps Timeout wraps CircuitBreaker:
    /// var pipeline = ResilienceStrategy.WithRetry&lt;string&gt;(retryOpts)
    ///     .Then(ResilienceStrategy.WithTimeout&lt;string&gt;(timeoutOpts))
    ///     .Then(ResilienceStrategy.WithCircuitBreaker&lt;string&gt;(breaker));
    ///
    /// // Execution: Retry → Timeout → CircuitBreaker → operation
    /// var result = await pipeline.Execute(ct =&gt; httpClient.GetStringAsync(url, ct));
    /// </code>
    /// </example>
    public static ResilienceStrategy<T> Then<T>(
        this ResilienceStrategy<T> outer,
        ResilienceStrategy<T> inner) =>
        (operation, cancellationToken) =>
            outer(
                async ct =>
                {
                    var innerResult = await inner(operation, ct).ConfigureAwait(false);
                    if (innerResult.IsErr)
                        throw new ResilienceResultException<T>(innerResult.Error);
                    return innerResult.Value;
                },
                cancellationToken);

    /// <summary>
    /// Conditionally applies the strategy. When the predicate returns <c>false</c>,
    /// the operation is executed directly without the strategy's resilience behavior.
    /// </summary>
    /// <remarks>
    /// Analogous to <see cref="ObserverExtensions.Where{TState,TEvent,TEffect}"/>
    /// — guards which invocations the strategy applies to.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Only retry on weekdays (silly but illustrative):
    /// var weekdayRetry = ResilienceStrategy.WithRetry&lt;string&gt;()
    ///     .Where(() =&gt; DateTime.UtcNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
    /// </code>
    /// </example>
    public static ResilienceStrategy<T> Where<T>(
        this ResilienceStrategy<T> strategy,
        Func<bool> predicate) =>
        (operation, cancellationToken) =>
            predicate()
                ? strategy(operation, cancellationToken)
                : Passthrough(operation, cancellationToken);

    /// <summary>
    /// Recovers from a strategy error by applying a handler function.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Analogous to <see cref="ObserverExtensions.Catch{TState,TEvent,TEffect}"/>
    /// — intercepts errors and decides whether to recover or propagate.
    /// </para>
    /// <para>
    /// The handler receives the <see cref="ResilienceError"/> and returns either
    /// <c>Ok(T)</c> (recovery value) or <c>Err(newError)</c> (replacement error).
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var withRecovery = ResilienceStrategy.WithRetry&lt;string&gt;()
    ///     .Catch(error =&gt; error.Reason is FailureReason.RetriesExhausted
    ///         ? Result&lt;string, ResilienceError&gt;.Ok("default")
    ///         : Result&lt;string, ResilienceError&gt;.Err(error));
    /// </code>
    /// </example>
    public static ResilienceStrategy<T> Catch<T>(
        this ResilienceStrategy<T> strategy,
        Func<ResilienceError, Result<T, ResilienceError>> handler) =>
        (operation, cancellationToken) =>
        {
            var task = strategy(operation, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                var result = task.Result;
                return result.IsErr
                    ? new ValueTask<Result<T, ResilienceError>>(handler(result.Error))
                    : task;
            }

            return AwaitThenCatch(task, handler);
        };

    /// <summary>
    /// Executes the composed pipeline by creating a <see cref="PipelineAutomaton{T}"/>
    /// and running it through <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect,TParameters}"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the terminal combinator and the specialized runtime for the
    /// pipeline automaton — the same pattern as <c>MvuRuntime.Start()</c> and
    /// <c>AggregateRunner.Handle()</c>:
    /// </para>
    /// <list type="number">
    ///     <item><description>Creates an <see cref="AutomatonRuntime{TAutomaton,TState,TEvent,TEffect,TParameters}"/>
    ///     with an Observer (OTel tracing) and an Interpreter (strategy execution).</description></item>
    ///     <item><description>Initialize emits <see cref="PipelineEffect{T}.RunPipeline"/> — the interpreter
    ///     runs the composed strategy and dispatches the result back as
    ///     <see cref="PipelineEvent{T}.OperationCompleted"/> or
    ///     <see cref="PipelineEvent{T}.OperationFailed"/>.</description></item>
    ///     <item><description>The automaton transitions to terminal state (<see cref="PipelineState{T}.Succeeded"/>
    ///     or <see cref="PipelineState{T}.Failed"/>).</description></item>
    ///     <item><description>The terminal state is extracted as <c>Result&lt;T, ResilienceError&gt;</c>.</description></item>
    /// </list>
    /// <para>
    /// Unhandled exceptions from the operation that escape all strategy layers are
    /// caught here and returned as <c>Err</c> with <see cref="FailureReason.Unknown"/>.
    /// </para>
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<Result<T, ResilienceError>> Execute<T>(
        this ResilienceStrategy<T> strategy,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        using var activity = ResilienceDiagnostics.Source.StartActivity("ResiliencePipeline.Execute");

        // ── Observer: OTel tracing on each transition ──
        Observer<PipelineState<T>, PipelineEvent<T>, PipelineEffect<T>> observer =
            (state, @event, effect) =>
            {
                activity?.AddEvent(new ActivityEvent(
                    @event.GetType().Name,
                    tags: new ActivityTagsCollection
                    {
                        ["pipeline.state"] = state.GetType().Name,
                        ["pipeline.effect"] = effect.GetType().Name
                    }));
                return PipelineResult.Ok;
            };

        // ── Interpreter: executes effects, feeds results back ──
        Interpreter<PipelineEffect<T>, PipelineEvent<T>> interpreter = effect => effect switch
        {
            PipelineEffect<T>.RunPipeline run =>
                InterpretRunPipeline(run.Strategy, run.Operation, cancellationToken),

            PipelineEffect<T>.ReportSuccess or
            PipelineEffect<T>.ReportFailure or
            PipelineEffect<T>.None =>
                new ValueTask<Result<PipelineEvent<T>[], PipelineError>>(
                    Result<PipelineEvent<T>[], PipelineError>.Ok([])),

            _ => new ValueTask<Result<PipelineEvent<T>[], PipelineError>>(
                Result<PipelineEvent<T>[], PipelineError>.Ok([]))
        };

        try
        {
            // Create and start the pipeline automaton runtime — Initialize interprets immediately
            var parameters = new PipelineParameters<T>(strategy, operation);
            using var runtime = await AutomatonRuntime<
                PipelineAutomaton<T>,
                PipelineState<T>,
                PipelineEvent<T>,
                PipelineEffect<T>,
                PipelineParameters<T>>
                .Start(parameters, observer, interpreter, threadSafe: false, trackEvents: false, cancellationToken)
                .ConfigureAwait(false);

            // Extract the terminal state
            return runtime.State switch
            {
                PipelineState<T>.Succeeded succeeded => Result<T, ResilienceError>.Ok(succeeded.Value),
                PipelineState<T>.Failed failed => Result<T, ResilienceError>.Err(failed.Error),
                _ => Result<T, ResilienceError>.Err(new ResilienceError(
                    $"Pipeline ended in unexpected state: {runtime.State.GetType().Name}",
                    "Pipeline",
                    FailureReason.Unknown))
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");

            return Result<T, ResilienceError>.Err(new ResilienceError(
                "Operation was cancelled.",
                "Pipeline",
                FailureReason.Cancelled));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            return Result<T, ResilienceError>.Err(new ResilienceError(
                $"Unhandled exception: {ex.Message}",
                "Pipeline",
                FailureReason.Unknown,
                ex));
        }
    }

    /// <summary>
    /// Interprets the <see cref="PipelineEffect{T}.RunPipeline"/> effect by executing
    /// the composed strategy and converting the result into a feedback event.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<PipelineEvent<T>[], PipelineError>> InterpretRunPipeline<T>(
        ResilienceStrategy<T> strategy,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await strategy(operation, cancellationToken).ConfigureAwait(false);

            PipelineEvent<T> feedbackEvent = result.IsOk
                ? new PipelineEvent<T>.OperationCompleted(result.Value)
                : new PipelineEvent<T>.OperationFailed(result.Error);

            return Result<PipelineEvent<T>[], PipelineError>.Ok([feedbackEvent]);
        }
        catch (ResilienceResultException<T> ex)
        {
            // Bridge exception from Then() composition — unwrap the error
            return Result<PipelineEvent<T>[], PipelineError>.Ok(
                [new PipelineEvent<T>.OperationFailed(ex.Error)]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<PipelineEvent<T>[], PipelineError>.Ok(
                [new PipelineEvent<T>.OperationFailed(new ResilienceError(
                    "Operation was cancelled.",
                    "Pipeline",
                    FailureReason.Cancelled))]);
        }
        catch (Exception ex)
        {
            return Result<PipelineEvent<T>[], PipelineError>.Ok(
                [new PipelineEvent<T>.OperationFailed(new ResilienceError(
                    $"Unhandled exception: {ex.Message}",
                    "Pipeline",
                    FailureReason.Unknown,
                    ex))]);
        }
    }

    // ── Internals ──

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<T, ResilienceError>> AwaitThenCatch<T>(
        ValueTask<Result<T, ResilienceError>> task,
        Func<ResilienceError, Result<T, ResilienceError>> handler)
    {
        var result = await task.ConfigureAwait(false);
        return result.IsErr ? handler(result.Error) : result;
    }

    /// <summary>
    /// The identity/passthrough strategy — executes the operation with no resilience wrapping.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<T, ResilienceError>> Passthrough<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        var result = await operation(cancellationToken).ConfigureAwait(false);
        return Result<T, ResilienceError>.Ok(result);
    }
}

// =============================================================================
// Factory methods — create strategies from existing resilience primitives
// =============================================================================

/// <summary>
/// Factory methods for creating <see cref="ResilienceStrategy{T}"/> delegates
/// from the individual resilience primitives.
/// </summary>
/// <remarks>
/// <para>
/// Each factory method wraps the corresponding ergonomic API (Retry.Execute,
/// Timeout.Execute, etc.) as a composable delegate. The resulting delegate
/// can be combined with other strategies via <see cref="ResilienceStrategyExtensions.Then{T}"/>.
/// </para>
/// <para>
/// The naming convention mirrors the core Automaton runtime: just as you build
/// an <see cref="Observer{TState,TEvent,TEffect}"/> pipeline with
/// <c>persist.Then(log).Catch(recover)</c>, you build a resilience pipeline with
/// <c>WithRetry().Then(WithTimeout()).Catch(fallback)</c>.
/// </para>
/// </remarks>
public static class ResilienceStrategy
{
    /// <summary>
    /// Creates a passthrough strategy that executes the operation with no resilience wrapping.
    /// This is the identity element for <see cref="ResilienceStrategyExtensions.Then{T}"/> composition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In monoidal terms, <c>Identity().Then(f) ≡ f</c> and <c>f.Then(Identity()) ≡ f</c>.
    /// Useful as a starting point for building pipelines incrementally.
    /// </para>
    /// </remarks>
    public static ResilienceStrategy<T> Identity<T>() =>
        async (operation, cancellationToken) =>
        {
            try
            {
                var result = await operation(cancellationToken).ConfigureAwait(false);
                return Result<T, ResilienceError>.Ok(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result<T, ResilienceError>.Err(new ResilienceError(
                    "Operation was cancelled.",
                    "Pipeline",
                    FailureReason.Cancelled));
            }
            catch (Exception ex)
            {
                return Result<T, ResilienceError>.Err(new ResilienceError(
                    $"Operation failed: {ex.Message}",
                    "Pipeline",
                    FailureReason.Unknown,
                    ex));
            }
        };

    /// <summary>
    /// Creates a retry strategy delegate.
    /// </summary>
    /// <example>
    /// <code>
    /// var pipeline = ResilienceStrategy.WithRetry&lt;string&gt;(new RetryOptions(MaxAttempts: 5))
    ///     .Then(ResilienceStrategy.WithTimeout&lt;string&gt;(new TimeoutOptions(TimeSpan.FromSeconds(10))));
    /// </code>
    /// </example>
    public static ResilienceStrategy<T> WithRetry<T>(RetryOptions? options = null) =>
        async (operation, cancellationToken) =>
            await Retry.Retry.Execute(operation, options, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a timeout strategy delegate.
    /// </summary>
    public static ResilienceStrategy<T> WithTimeout<T>(TimeoutOptions? options = null) =>
        async (operation, cancellationToken) =>
            await Timeout.Timeout.Execute(operation, options, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a circuit breaker strategy delegate.
    /// </summary>
    /// <remarks>
    /// The circuit breaker is stateful — the same <paramref name="breaker"/> instance
    /// must be shared across all invocations to track failure state.
    /// </remarks>
    public static ResilienceStrategy<T> WithCircuitBreaker<T>(CircuitBreaker.CircuitBreaker breaker) =>
        async (operation, cancellationToken) =>
            await breaker.Execute(operation, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a rate limiter strategy delegate.
    /// </summary>
    /// <remarks>
    /// The rate limiter is stateful — the same <paramref name="limiter"/> instance
    /// must be shared across all invocations to track token availability.
    /// </remarks>
    public static ResilienceStrategy<T> WithRateLimiter<T>(RateLimiter.RateLimiter limiter) =>
        async (operation, cancellationToken) =>
            await limiter.Execute(operation, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a fallback strategy delegate with an async fallback operation.
    /// </summary>
    public static ResilienceStrategy<T> WithFallback<T>(Func<CancellationToken, ValueTask<T>> fallback) =>
        async (operation, cancellationToken) =>
            await Fallback.Fallback.Execute(operation, fallback, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates a fallback strategy delegate with a static fallback value.
    /// </summary>
    public static ResilienceStrategy<T> WithFallback<T>(T fallbackValue) =>
        async (operation, cancellationToken) =>
            await Fallback.Fallback.Execute(operation, fallbackValue, cancellationToken).ConfigureAwait(false);
}

// =============================================================================
// Bridge exception — internal only, used by Then() composition
// =============================================================================

/// <summary>
/// Internal bridge exception used by <see cref="ResilienceStrategyExtensions.Then{T}"/>
/// to propagate <see cref="ResilienceError"/> through the delegate boundary.
/// </summary>
/// <remarks>
/// <para>
/// When composing strategies with <c>Then()</c>, the inner strategy returns
/// <c>Result&lt;T, ResilienceError&gt;</c>, but the outer strategy expects
/// <c>Func&lt;CancellationToken, ValueTask&lt;T&gt;&gt;</c> — a function that
/// returns <c>T</c>, not <c>Result&lt;T&gt;</c>.
/// </para>
/// <para>
/// This exception bridges that gap: when the inner strategy returns <c>Err</c>,
/// the bridge throws this exception, which is caught by
/// <see cref="ResilienceStrategyExtensions.Execute{T}"/> and unwrapped back
/// into <c>Result&lt;T, ResilienceError&gt;</c>.
/// </para>
/// <para>
/// This is the same structural problem that Haskell's IO monad and
/// <c>ExceptT</c> transformer solve — threading typed errors through an
/// untyped boundary. The bridge exception is minimal and internal-only.
/// </para>
/// </remarks>
internal sealed class ResilienceResultException<T>(ResilienceError error) : Exception
{
    /// <summary>The structured resilience error being propagated.</summary>
    public ResilienceError Error { get; } = error;
}
