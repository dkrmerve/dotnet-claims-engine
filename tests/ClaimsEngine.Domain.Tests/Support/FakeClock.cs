namespace ClaimsEngine.Domain.Tests.Support;

/// <summary>A fixed instant. Domain methods receive "now" as a parameter, so no interface is needed here.</summary>
internal static class FakeClock
{
    public static readonly DateTimeOffset Now = new(2026, 3, 15, 10, 30, 0, TimeSpan.Zero);

    public static DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    public static DateTimeOffset Plus(TimeSpan span) => Now + span;
}
