namespace S3Harp.TestSupport;

/// <summary>A clock that always reads the same instant, so timestamps in tests are exact.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
