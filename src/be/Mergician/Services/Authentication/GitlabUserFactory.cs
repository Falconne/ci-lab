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

    private readonly ILogger<GitlabUserFactory> _logger;

    private readonly string? _serviceToken;

    public bool IsServiceTokenConfigured => !string.IsNullOrWhiteSpace(_serviceToken);

    public GitlabUserFactory(string apiBaseUrl, string? serviceToken, ILogger<GitlabUserFactory> logger)
    {
        _apiBaseUrl = apiBaseUrl;
        _serviceToken = serviceToken;
        _logger = logger;

        if (!IsServiceTokenConfigured)
            _logger.LogWarning("GitLab service token is not configured. Set the Mergician:GitLab:ServiceToken setting");
        else
            _logger.LogInformation("GitLab service user configured");
    }

    /// <summary>
    ///     Returns a GitlabAccessUser for the configured service account.
    ///     Throws if the service token is not configured because this indicates
    ///     a misconfiguration that should result in a server error.
    /// </summary>
    public GitlabAccessUser GetServiceUser()
    {
        if (!IsServiceTokenConfigured)
        {
            _logger.LogError("GitLab service token is not configured — service user cannot be created");
            throw new InvalidOperationException("GitLab service token is not configured");
        }

        return new GitlabAccessUser(_serviceToken!, _apiBaseUrl);
    }
}
