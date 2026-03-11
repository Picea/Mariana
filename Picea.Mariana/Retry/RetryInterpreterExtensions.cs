// =============================================================================
// Retry Interpreter Extensions — compose retry into Automaton pipelines
// =============================================================================

using System.Runtime.CompilerServices;
using Picea.Mariana.Retry;

namespace Picea.Mariana;

/// <summary>
/// Interpreter combinators for integrating retry logic into Automaton pipelines.
/// </summary>
public static class RetryInterpreterExtensions
{
    /// <summary>
    /// Wraps an interpreter with retry logic: if the interpreter returns
    /// <c>Err</c>, retries according to the specified options.
    /// </summary>
    public static Interpreter<TEffect, TEvent> WithRetry<TEffect, TEvent>(
        this Interpreter<TEffect, TEvent> interpreter,
        RetryOptions? options = null) =>
        effect =>
        {
            var result = Retry.Retry.Execute(
                async ct =>
                {
                    var interpreterResult = await interpreter(effect).ConfigureAwait(false);
                    if (interpreterResult.IsErr)
                    {
                        throw new PipelineErrorException(interpreterResult.Error);
                    }

                    return interpreterResult.Value;
                },
                options);

            if (result.IsCompletedSuccessfully)
            {
                var r = result.Result;
                return r.IsOk
                    ? new ValueTask<Result<TEvent[], PipelineError>>(
                        Result<TEvent[], PipelineError>.Ok(r.Value))
                    : new ValueTask<Result<TEvent[], PipelineError>>(
                        Result<TEvent[], PipelineError>.Err(
                            UnwrapPipelineError(r.Error)));
            }

            return AwaitRetryResult(result);
        };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<Result<TEvent[], PipelineError>> AwaitRetryResult<TEvent>(
        ValueTask<Result<TEvent[], ResilienceError>> retryTask)
    {
        var r = await retryTask.ConfigureAwait(false);

        return r.IsOk
            ? Result<TEvent[], PipelineError>.Ok(r.Value)
            : Result<TEvent[], PipelineError>.Err(
                UnwrapPipelineError(r.Error));
    }

    private static PipelineError UnwrapPipelineError(ResilienceError error) =>
        error.Exception is PipelineErrorException pex
            ? pex.PipelineError
            : new PipelineError(error.Message, "Retry", error.Exception);
}

/// <summary>
/// Internal exception wrapper to bridge PipelineError through the retry loop.
/// </summary>
internal sealed class PipelineErrorException(PipelineError error) : Exception(error.Message)
{
    /// <summary>The wrapped pipeline error.</summary>
    public PipelineError PipelineError { get; } = error;
}
