using Mergician.Entities;

namespace Mergician.Services;

/// <summary>
///     Coordinates and publishes the application's startup state.
///     Background workers call into this service to update the current startup status.
///     The service exposes a snapshot <see cref="StartupStatus" /> for the frontend to poll.
///     GitLab recovery signalling is handled by <see cref="GitLab.GitLabHealthService" />.
/// </summary>
public class StartupStateService
{
    private volatile StartupStatus _status = new() { IsReady = false, Message = "Starting up..." };

    public StartupStatus GetStatus()
    {
        return _status;
    }

    public void SetStatus(bool isReady, string message, string? error = null, bool isGitLabRecovery = false)
    {
        _status = new StartupStatus
        {
            IsReady = isReady,
            Message = message,
            Error = error,
            IsGitLabRecovery = isGitLabRecovery
        };
    }
}