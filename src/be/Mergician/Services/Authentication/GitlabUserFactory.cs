using Serilog;

namespace Mergician.Services.Authentication;

/// <summary>
///     Factory for creating GitlabAccessUser instances for the service user.
///     The service user is used by the backend for background maintenance tasks
///     and does not require an HTTP context. Current user authentication is
///     handled by the GitLabCookieAuthenticationHandler instead.
/// </summary>
public class GitlabUserFactory
{
    private readonly string _apiBaseUrl;
    private readonly string? _serviceToken;

    public bool IsServiceTokenConfigured => !string.IsNullOrWhiteSpace(_serviceToken);

    public GitlabUserFactory(string apiBaseUrl, string? serviceToken)
    {
        _apiBaseUrl = apiBaseUrl;
        _serviceToken = serviceToken;

        if (!IsServiceTokenConfigured)
            Log.Warning("GitLab service token is not configured. Set the Mergician:GitLab:ServiceToken setting");
        else
            Log.Information("GitLab service user configured");
    }

    /// <summary>
    ///     Returns a GitlabAccessUser for the configured service account,
    ///     or null if the service token is not configured.
    /// </summary>
    public GitlabAccessUser? GetServiceUser()
    {
        if (!IsServiceTokenConfigured)
        {
            Log.Debug("GitLab service token is not configured — cannot create service user");
            return null;
        }

        return new GitlabAccessUser(_serviceToken!, _apiBaseUrl);
    }
}
