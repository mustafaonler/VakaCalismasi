namespace AtmCash.IntegrationTests.Infrastructure;

/// <summary>A clock the test can move forward, e.g. past a quote's 60-second validity.</summary>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = TimeProvider.System.GetUtcNow();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
