using Util;

namespace Mergician.Services.Authentication;

/// <summary>
///     Factory for creating <see cref="AccessDetailsForUser" /> instances for the service user.
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
    ///     Returns an <see cref="AccessDetailsForUser" /> for the configured service account.
    ///     The UserId is 0 because the service user has no specific GitLab user identity.
    ///     Throws if the service token is not configured because this indicates
    ///     a misconfiguration that should result in a server error.
    /// </summary>
    public AccessDetailsForUser GetServiceUser()
    {
        if (_serviceToken.IsEmpty())
        {
            throw new InvalidOperationException("GitLab service token is not configured");
        }

        return new AccessDetailsForUser(_serviceToken, _apiBaseUrl, 0);
    }
}