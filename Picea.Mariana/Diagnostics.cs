// =============================================================================
// Diagnostics — OpenTelemetry-compatible Tracing for Resilience
// =============================================================================
// Provides an ActivitySource for distributed tracing instrumentation.
//
// To enable tracing in your application, register the source name:
//
//     builder.Services.AddOpenTelemetry()
//         .WithTracing(tracing => tracing.AddSource(ResilienceDiagnostics.SourceName));
// =============================================================================

using System.Diagnostics;

namespace Picea.Mariana;

/// <summary>
/// OpenTelemetry-compatible tracing for Picea.Mariana resilience strategies.
/// </summary>
/// <remarks>
/// <para>
/// Exposes a single <see cref="ActivitySource"/> that all resilience strategies
/// use to emit spans. Application developers enable collection by registering
/// <see cref="SourceName"/> with their telemetry pipeline.
/// </para>
/// </remarks>
public static class ResilienceDiagnostics
{
    /// <summary>
    /// The ActivitySource name. Use this to register the source with your
    /// telemetry pipeline (e.g., <c>AddSource(ResilienceDiagnostics.SourceName)</c>).
    /// </summary>
    public const string SourceName = "Picea.Mariana";

    /// <summary>
    /// The shared ActivitySource for all resilience strategy tracing.
    /// </summary>
    internal static ActivitySource Source { get; } = new(
        SourceName,
        typeof(ResilienceDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
