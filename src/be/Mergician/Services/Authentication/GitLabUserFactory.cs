using Util;

namespace Mergician.Services.Authentication;

/// <summary>
///     Factory for creating <see cref="AccessDetailsBase" /> instances for the service user.
///     The service user is used by the backend for background maintenance tasks
///     and does not require an HTTP context. Current user authentication is
///     handled by the GitLabCookieAuthenticationHandler instead.
/// </summary>
public class GitLabUserFactory
{
    private readonly string _apiBaseUrl;

    private readonly string? _serviceToken;

    public GitLabUserFactory(string apiBaseUrl, string? serviceToken)
    {
        _apiBaseUrl = apiBaseUrl;
        _serviceToken = serviceToken;
    }

    /// <summary>
    ///     Returns an <see cref="AccessDetailsBase" /> for the configured service account.
    ///     Throws if the service token is not configured because this indicates
    ///     a misconfiguration that should result in a server error.
    /// </summary>
    public AccessDetailsBase GetServiceUser()
    {
        if (_serviceToken.IsEmpty())
        {
            throw new InvalidOperationException("GitLab service token is not configured");
        }

        return new AccessDetailsBase(_serviceToken, _apiBaseUrl);
    }
}