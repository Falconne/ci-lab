using System.Net.Http.Headers;

namespace Mergician.Services.Authentication;

/// <summary>
///     Represents an authenticated GitLab API user with a valid access token, a known GitLab user ID,
///     and the API base URL pre-configured so consumers only need to specify the relative path.
///     Instances are created by the GitLabCookieAuthenticationHandler after successful authentication.
///     A UserId of 0 indicates a service or system user with no specific GitLab user identity.
/// </summary>
public class AccessDetailsForUser
{
    private readonly string _accessToken;

    private readonly string _apiBaseUrl;

    public AccessDetailsForUser(string accessToken, string apiBaseUrl, int userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiBaseUrl);

        _accessToken = accessToken;
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        UserId = userId;
    }

    public int UserId { get; }

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