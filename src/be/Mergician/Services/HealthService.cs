using Mergician.Entities;

namespace Mergician.Services;

/// <summary>
///     Coordinates and publishes the application's health state.
///     Background workers call into this service to update the current health status.
///     The service exposes a snapshot <see cref="HealthStatus" /> for the frontend to poll.
///     GitLab recovery signalling is handled by <see cref="GitLab.GitLabHealthService" />.
/// </summary>
public class HealthService
{
    private volatile HealthStatus _status = new() { IsReady = false, Message = "Starting up..." };

    public HealthStatus GetStatus()
    {
        return _status;
    }

    public void SetStatus(bool isReady, string message, string? error = null, bool isGitLabRecovery = false)
    {
        _status = new HealthStatus
        {
            IsReady = isReady,
            Message = message,
            Error = error,
            IsGitLabRecovery = isGitLabRecovery
        };
    }
}