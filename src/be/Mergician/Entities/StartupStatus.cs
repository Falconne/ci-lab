namespace Mergician.Entities;

public record StartupStatus
{
    public bool IsReady { get; init; }
    public string Message { get; init; } = "Starting up...";
    public string? Error { get; init; }
}
