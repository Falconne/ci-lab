namespace Mergician.Entities;

public record HealthStatus
{
    public string Status { get; init; } = "healthy";
    public List<string> ConfigurationErrors { get; init; } = new List<string>();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
