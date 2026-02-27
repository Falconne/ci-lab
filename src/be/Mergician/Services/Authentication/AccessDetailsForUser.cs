using System.Net.Http.Headers;

namespace Mergician.Services.Authentication;

/// <summary>
///     Represents an authenticated GitLab API user with a valid access token.
///     Provides HTTP request creation with the GitLab API v4 base URL pre-configured,
///     so consumers only need to specify the relative API path (e.g. "user", "projects/1").
///     Instances are created by the GitLabCookieAuthenticationHandler (for the current
///     OAuth user) or by GitlabUserFactory (for the service user).
/// </summary>
public class AccessDetailsForUser
{
    private readonly string _accessToken;

    private readonly string _apiBaseUrl;

    public AccessDetailsForUser(string accessToken, string apiBaseUrl, int? userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiBaseUrl);

        _accessToken = accessToken;
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        UserId = userId;
    }

    // TODO instead of allowing UserId to be nullable, split this class into a base (called AccessDetailsBase) and derived, where the
    // base doesn't need a userid. Use the base class for the temp instance needed to fetch userid and use the derived
    // class everywhere else.
    public int? UserId { get; }

    /// <summary>
    ///     Creates an authenticated HttpRequestMessage for the given method and relative API path.
    /// </summary>
    /// <param name="method">The HTTP method (GET, POST, etc.)</param>
    /// <param name="path">Relative API path, e.g. "user" or "projects/1/repository/branches"</param>
    public HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var url = $"{_apiBaseUrl}/{path.TrimStart('/')}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return request;
    }
}