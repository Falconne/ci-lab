namespace Mergician.Entities;

public record HealthStatus
{
    public bool IsReady { get; init; }

    public string Message { get; init; } = "Starting up...";

    public string? Error { get; init; }

    /// <summary>
    ///     True when the application was previously ready but GitLab became unreachable,
    ///     causing the app to enter a recovery polling state. The frontend uses this to
    ///     distinguish "waiting for GitLab to recover" from a normal cold start.
    /// </summary>
    public bool IsGitLabRecovery { get; init; }
}