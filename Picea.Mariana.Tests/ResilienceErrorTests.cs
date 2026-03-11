namespace Picea.Mariana.Tests;

public class ResilienceErrorTests
{
    [Fact]
    public void ToString_includes_strategy_and_reason()
    {
        var error = new ResilienceError(
            "All attempts failed",
            "Retry",
            FailureReason.RetriesExhausted);

        Assert.Equal("[Retry:RetriesExhausted] All attempts failed", error.ToString());
    }

    [Fact]
    public void ToString_includes_unknown_reason_by_default()
    {
        var error = new ResilienceError("Something went wrong", "Test");

        Assert.Equal("[Test:Unknown] Something went wrong", error.ToString());
    }

    [Fact]
    public void Exception_is_null_by_default()
    {
        var error = new ResilienceError("fail", "Test");

        Assert.Null(error.Exception);
    }

    [Fact]
    public void Exception_is_preserved()
    {
        var ex = new InvalidOperationException("inner");
        var error = new ResilienceError("fail", "Test", Exception: ex);

        Assert.Same(ex, error.Exception);
    }
}
