using Bootstrap.Entities.Gitlab;
using RestSharp;
using Serilog;

namespace Bootstrap.Services.Gitlab;

// This service handles initial configuration and setup of a freshly installed GitLab server.
// For project creation and management operations, use GitlabService instead.
public class GitlabBootstrapService : IDisposable
{
    private readonly RestClient _client;

    private readonly string _token;

    public GitlabBootstrapService(string gitlabUrl, string token)
    {
        GitlabUrl = gitlabUrl.TrimEnd('/');
        _token = token;
        _client = new RestClient(
            new RestClientOptions($"{GitlabUrl}/api/v4")
            {
                ThrowOnAnyError = false,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                Timeout = TimeSpan.FromSeconds(30)
            });

        _client.AddDefaultHeader("PRIVATE-TOKEN", token);
    }

    public string GitlabUrl { get; }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<bool> ValidateGitlabToken()
    {
        var request = new RestRequest("user");
        var fullUrl = _client.BuildUri(request);
        Log.Debug($"Validating token at URL: {fullUrl}");
        Log.Debug($"Using token: [{_token}] (length: {_token.Length})");

        var response = await _client.ExecuteGetAsync<GitlabUser>(request);

        if (response is { IsSuccessful: true, Data: not null })
        {
            Log.Information($"Authenticated as: {response.Data.Username}");
            return true;
        }

        Log.Error($"Gitlab token validation failed: {(int)response.StatusCode} {response.StatusCode}");
        Log.Error($"Request URL: {response.ResponseUri}");
        if (!string.IsNullOrWhiteSpace(response.Content) && response.Content.Length < 500)
        {
            Log.Error($"Response: {response.Content}");
        }

        return false;
    }
}
