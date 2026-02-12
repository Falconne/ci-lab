namespace Mergician.Entities;

public record HealthStatus
{
    public string Status { get; init; } = "healthy";
    public List<string> ConfigurationErrors { get; init; } = [];
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
