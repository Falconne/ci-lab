using System.Net.Http.Headers;

namespace Mergician.Services.Authentication;

/// <summary>
///     Represents authenticated GitLab API credentials with a valid access token.
///     Provides HTTP request creation with the GitLab API v4 base URL pre-configured,
///     so consumers only need to specify the relative API path (e.g. "user", "projects/1").
///     Use <see cref="AccessDetailsForUser" /> when the GitLab user ID is also needed.
/// </summary>
public class AccessDetailsBase
{
    private readonly string _accessToken;

    private readonly string _apiBaseUrl;

    public AccessDetailsBase(string accessToken, string apiBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiBaseUrl);

        _accessToken = accessToken;
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
    }

    /// <summary>
    ///     Creates an authenticated HttpRequestMessage for the given method and relative API path.
    /// </summary>
    /// <param name="query">Relative API path, e.g. "user" or "projects/1/repository/branches"</param>
    /// <param name="method">The HTTP method (GET, POST, etc.)</param>
    public HttpRequestMessage CreateRequest(string query, HttpMethod method)
    {
        var url = $"{_apiBaseUrl}/{query.TrimStart('/')}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return request;
    }

    public HttpRequestMessage CreateRequest(string query)
    {
        return CreateRequest(query, HttpMethod.Get);
    }
}