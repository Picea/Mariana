namespace Picea.Mariana.Tests;

public class BackoffTests
{
    // =========================================================================
    // Constant backoff
    // =========================================================================

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public void Constant_returns_base_delay_regardless_of_attempt(int attempt)
    {
        var baseDelay = TimeSpan.FromSeconds(2);
        var result = Backoff.Compute(BackoffType.Constant, baseDelay, attempt);

        Assert.Equal(baseDelay, result);
    }

    // =========================================================================
    // Linear backoff
    // =========================================================================

    [Fact]
    public void Linear_scales_linearly_with_attempt()
    {
        var baseDelay = TimeSpan.FromMilliseconds(100);

        Assert.Equal(TimeSpan.FromMilliseconds(100), Backoff.Compute(BackoffType.Linear, baseDelay, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), Backoff.Compute(BackoffType.Linear, baseDelay, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(300), Backoff.Compute(BackoffType.Linear, baseDelay, 3));
        Assert.Equal(TimeSpan.FromMilliseconds(500), Backoff.Compute(BackoffType.Linear, baseDelay, 5));
    }

    [Fact]
    public void Linear_respects_max_delay_cap()
    {
        var baseDelay = TimeSpan.FromSeconds(5);
        var maxDelay = TimeSpan.FromSeconds(10);

        var result = Backoff.Compute(BackoffType.Linear, baseDelay, 10, maxDelay);

        Assert.Equal(maxDelay, result);
    }

    // =========================================================================
    // Exponential backoff
    // =========================================================================

    [Fact]
    public void Exponential_doubles_each_attempt()
    {
        var baseDelay = TimeSpan.FromMilliseconds(100);
        var maxDelay = TimeSpan.FromMinutes(10);

        Assert.Equal(TimeSpan.FromMilliseconds(100), Backoff.Compute(BackoffType.Exponential, baseDelay, 1, maxDelay));
        Assert.Equal(TimeSpan.FromMilliseconds(200), Backoff.Compute(BackoffType.Exponential, baseDelay, 2, maxDelay));
        Assert.Equal(TimeSpan.FromMilliseconds(400), Backoff.Compute(BackoffType.Exponential, baseDelay, 3, maxDelay));
        Assert.Equal(TimeSpan.FromMilliseconds(800), Backoff.Compute(BackoffType.Exponential, baseDelay, 4, maxDelay));
    }

    [Fact]
    public void Exponential_respects_max_delay_cap()
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(10);

        // Attempt 5: 1 × 2^4 = 16s, capped at 10s
        var result = Backoff.Compute(BackoffType.Exponential, baseDelay, 5, maxDelay);

        Assert.Equal(maxDelay, result);
    }

    // =========================================================================
    // Decorrelated jitter
    // =========================================================================

    [Fact]
    public void DecorrelatedJitter_returns_non_negative()
    {
        var baseDelay = TimeSpan.FromMilliseconds(100);

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var result = Backoff.Compute(BackoffType.DecorrelatedJitter, baseDelay, attempt);
            Assert.True(result >= TimeSpan.Zero, $"Jitter delay was negative on attempt {attempt}");
        }
    }

    [Fact]
    public void DecorrelatedJitter_respects_max_delay_cap()
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(5);

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var result = Backoff.Compute(BackoffType.DecorrelatedJitter, baseDelay, attempt, maxDelay);
            Assert.True(result <= maxDelay, $"Jitter delay {result} exceeded cap {maxDelay} on attempt {attempt}");
        }
    }

    [Fact]
    public void DecorrelatedJitter_produces_varying_delays()
    {
        var baseDelay = TimeSpan.FromMilliseconds(100);
        var delays = new HashSet<double>();

        for (var i = 0; i < 50; i++)
            delays.Add(Backoff.Compute(BackoffType.DecorrelatedJitter, baseDelay, 3).TotalMilliseconds);

        // With 50 samples and random jitter, we should see at least a few distinct values
        Assert.True(delays.Count > 1, "Decorrelated jitter should produce varying delays");
    }

    // =========================================================================
    // Default max delay
    // =========================================================================

    [Fact]
    public void Default_max_delay_is_30_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Backoff.MaxDelay);
    }

    // =========================================================================
    // Edge cases
    // =========================================================================

    [Fact]
    public void Zero_base_delay_produces_zero_for_all_strategies()
    {
        var zero = TimeSpan.Zero;

        Assert.Equal(zero, Backoff.Compute(BackoffType.Constant, zero, 5));
        Assert.Equal(zero, Backoff.Compute(BackoffType.Linear, zero, 5));
        Assert.Equal(zero, Backoff.Compute(BackoffType.Exponential, zero, 5));
        Assert.Equal(zero, Backoff.Compute(BackoffType.DecorrelatedJitter, zero, 5));
    }
}
