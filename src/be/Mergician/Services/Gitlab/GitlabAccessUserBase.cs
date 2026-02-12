using System.Net.Http.Headers;
using Serilog;

namespace Mergician.Services.Gitlab;

/// <summary>
/// Base class for GitLab API access. Provides authenticated HTTP request creation
/// with the GitLab API v4 base URL pre-configured, so subclasses and consumers
/// only need to specify the relative API path (e.g. "user", "projects/1").
/// </summary>
public abstract class GitlabAccessUserBase
{
    private readonly string _apiBaseUrl;

    protected GitlabAccessUserBase(string apiBaseUrl)
    {
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Returns a valid GitLab access token, or null if unavailable.
    /// </summary>
    public abstract Task<string?> GetValidAccessToken();

    /// <summary>
    /// Creates an authenticated HttpRequestMessage for the given method and relative API path.
    /// Returns null if no valid access token is available.
    /// </summary>
    /// <param name="method">The HTTP method (GET, POST, etc.)</param>
    /// <param name="path">Relative API path, e.g. "user" or "projects/1/repository/branches"</param>
    public async Task<HttpRequestMessage?> CreateRequest(HttpMethod method, string path)
    {
        var accessToken = await GetValidAccessToken();
        if (accessToken == null)
        {
            Log.Debug("No valid access token available, cannot create request for {Path}", path);
            return null;
        }

        return CreateRequestWithToken(method, path, accessToken);
    }

    /// <summary>
    /// Creates an authenticated HttpRequestMessage using a specific token,
    /// bypassing GetValidAccessToken(). Useful for token validation flows.
    /// </summary>
    protected HttpRequestMessage CreateRequestWithToken(HttpMethod method, string path, string accessToken)
    {
        var url = $"{_apiBaseUrl}/{path.TrimStart('/')}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}
