# Picea.Mariana

Production-grade resilience strategies modeled as Mealy machine automata — built on the [Picea](https://github.com/Picea/Picea) kernel.

## What is Mariana?

Mariana provides resilience patterns where each strategy (retry, circuit breaker, etc.) is a Mealy machine automaton. This means:

- **Strategies are pure state machines** — deterministic, testable, composable
- **State transitions are explicit** — you can inspect and trace the strategy's behavior
- **No hidden threading primitives** — the kernel handles concurrency
- **OpenTelemetry built-in** — every strategy emits spans via `ActivitySource`

## Strategies

| Strategy | Description |
|----------|-------------|
| **Retry** | Retry failed operations with configurable backoff (constant, linear, exponential, decorrelated jitter) |
| **Circuit Breaker** | Stop calling a failing dependency; trip → half-open → close cycle |
| **Timeout** | Fail fast when an operation exceeds a time budget |
| **Fallback** | Provide a substitute result when the primary operation fails |
| **Rate Limiter** | Token-bucket rate limiting to protect downstream services |
| **Hedging** | Race parallel attempts against a deadline for latency-sensitive paths |

## Composition

Strategies compose into **pipelines** — sequential chains where each strategy wraps the next:

```csharp
// Retry → Circuit Breaker → Timeout
var pipeline = Pipeline.Create(
    retryStrategy,
    circuitBreakerStrategy,
    timeoutStrategy
);
```

## Installation

```bash
dotnet add package Picea.Mariana
```

## The Picea Ecosystem

| Package | Description | Repo |
|---------|-------------|------|
| [Picea](https://github.com/picea/picea) | Core kernel: `Automaton<>`, `Result<>`, `Decider<>` | [picea/picea](https://github.com/picea/picea) |
| **Picea.Mariana** | Resilience patterns (this repo) | [picea/mariana](https://github.com/picea/mariana) |
| [Picea.Abies](https://github.com/picea/abies) | MVU framework for Blazor | [picea/abies](https://github.com/picea/abies) |
| [Picea.Glauca](https://github.com/picea/glauca) | Event Sourcing patterns | [picea/glauca](https://github.com/picea/glauca) |
| [Picea.Rubens](https://github.com/picea/rubens) | Actor model patterns | [picea/rubens](https://github.com/picea/rubens) |

## License

[Apache-2.0](LICENSE) — Copyright 2025 Maurice Peters
