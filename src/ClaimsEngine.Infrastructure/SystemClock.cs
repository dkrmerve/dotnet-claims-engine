using ClaimsEngine.Application.Ports;

namespace ClaimsEngine.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
