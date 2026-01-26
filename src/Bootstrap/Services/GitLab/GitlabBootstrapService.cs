using Bootstrap.Entities.Gitlab;
using Bootstrap.Services.Utilities;
using RestSharp;
using Serilog;

namespace Bootstrap.Services.Gitlab;

// This service handles initial configuration and setup of a freshly installed GitLab server.
// For project creation and management operations, use GitlabService instead.
public class GitlabBootstrapService : IDisposable
{
    private readonly RestClient _client;
    private readonly EnvFileService _envFileService;
    private readonly string _gitlabUrl;
    private string? _token;

    public GitlabBootstrapService(string gitlabUrl, EnvFileService envFileService)
    {
        _gitlabUrl = gitlabUrl.TrimEnd('/');
        _envFileService = envFileService;
        _client = new RestClient(
            new RestClientOptions($"{_gitlabUrl}/api/v4")
            {
                ThrowOnAnyError = false,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                Timeout = TimeSpan.FromSeconds(30)
            });
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<bool> Execute()
    {
        Log.Information("Starting automated GitLab setup");

        // Ensure GitLab is available before attempting token operations
        Log.Information("Waiting for Gitlab to become available...");
        var gitlabReady = await HttpHelper.WaitForService(_gitlabUrl, TimeSpan.FromMinutes(5));
        if (!gitlabReady)
        {
            Log.Error("GitLab did not become available; exiting");
            return false;
        }

        // Get and validate GitLab token
        _token = await GetAndValidateGitlabToken();
        if (string.IsNullOrEmpty(_token))
        {
            Log.Error("Failed to obtain valid GitLab token; exiting");
            return false;
        }

        // Set token header for API calls
        _client.AddDefaultHeader("PRIVATE-TOKEN", _token);

        Log.Information("GitLab initial setup completed");
        return true;
    }

    private async Task<string?> GetAndValidateGitlabToken()
    {
        var timeout = TimeSpan.FromMinutes(7);
        var deadline = DateTime.UtcNow + timeout;
        var pollInterval = TimeSpan.FromSeconds(5);

        Log.Information($"Waiting up to {timeout.TotalMinutes} minutes for GITLAB_TOKEN in .env...");

        while (DateTime.UtcNow < deadline)
        {
            // Reload .env to pick up tokens written by external processes
            _envFileService.Load();
            var token = _envFileService.GetValue("GITLAB_TOKEN");

            if (!string.IsNullOrEmpty(token))
            {
                Log.Information("Found GITLAB_TOKEN in .env; validating...");
                try
                {
                    if (await ValidateGitlabToken(token))
                    {
                        Log.Information("Gitlab token is valid");
                        _envFileService.SaveOrUpdateEnvFile("GITLAB_TOKEN", token);
                        return token;
                    }

                    Log.Information("GITLAB_TOKEN present but not valid yet; will retry until timeout");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error validating Gitlab token: {ex.Message}");
                }
            }
            else
            {
                Log.Information("No GITLAB_TOKEN found yet; polling .env...");
            }

            await Task.Delay(pollInterval);
        }

        Log.Error($"Timed out waiting for a valid GITLAB_TOKEN after {timeout.TotalMinutes} minutes");
        return null;
    }

    private async Task<bool> ValidateGitlabToken(string token)
    {
        var request = new RestRequest("user");
        request.AddHeader("PRIVATE-TOKEN", token);
        var fullUrl = _client.BuildUri(request);
        Log.Debug($"Validating token at URL: {fullUrl}");
        Log.Debug($"Using token: [{token}] (length: {token.Length})");

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
