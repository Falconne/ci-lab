namespace Mergician.Entities;

public record HealthStatus
{
    public string Status { get; init; } = "healthy";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
