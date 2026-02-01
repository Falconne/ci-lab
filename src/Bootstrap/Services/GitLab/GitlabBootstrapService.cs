using Bootstrap.Entities.Gitlab;
using Bootstrap.Utilities;
using RestSharp;
using Serilog;
using System.Net;

namespace Bootstrap.Services.Gitlab;

// This service handles initial configuration and setup of a freshly installed GitLab server.
// For project creation and management operations, use GitlabService instead.
public class GitlabBootstrapService : IDisposable
{
    private readonly RestClient _client;
    private readonly EnvFileService _envFileService;
    private readonly string _gitlabURL;
    private string? _token;

    public GitlabBootstrapService(string gitlabURL, EnvFileService envFileService)
    {
        _gitlabURL = gitlabURL.TrimEnd('/');
        _envFileService = envFileService;
        _client = new RestClient(
            new RestClientOptions($"{_gitlabURL}/api/v4")
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

    public async Task Execute()
    {
        Log.Information("Starting automated GitLab setup");

        // Ensure GitLab is available before attempting token operations
        Log.Information("Waiting for Gitlab to become available...");
        await ReliabilityHelpers.WaitForService(_gitlabURL, TimeSpan.FromMinutes(5));

        // Get and validate GitLab token
        _token = await GetAndValidateGitlabToken();

        // Set token header for API calls
        _client.AddDefaultHeader("PRIVATE-TOKEN", _token);

        // Configure GitLab to use 'main' as the default branch
        await ConfigureDefaultBranch();

        // Create Bob Builder user
        await CreateBobBuilderUser();

        // Create test accounts
        await CreateTestAccounts();

        Log.Information("GitLab initial setup completed");
    }

    private async Task ConfigureDefaultBranch()
    {
        Log.Information("Configuring GitLab to use 'main' as the default branch...");

        var request = new RestRequest("application/settings", Method.Put)
            .AddJsonBody(new { default_branch_name = "main" });

        var response = await _client.ExecuteAsync(request);

        if (response.IsSuccessful)
        {
            Log.Information("GitLab default branch set to 'main'");
        }
        else
        {
            var msg = $"Failed to set GitLab default branch: {(int)response.StatusCode} - {response.Content}";
            Log.Error(msg);
            throw new InvalidOperationException(msg);
        }
    }

    private async Task<string> GetAndValidateGitlabToken()
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
                    Log.Error($"Error validating Gitlab token: {ex.Message}");
                    throw;
                }
            }
            else
            {
                Log.Information("No GITLAB_TOKEN found yet; polling .env...");
            }

            await Task.Delay(pollInterval);
        }

        Log.Error($"Timed out waiting for a valid GITLAB_TOKEN after {timeout.TotalMinutes} minutes");
        throw new InvalidOperationException($"Timed out waiting for a valid GITLAB_TOKEN after {timeout.TotalMinutes} minutes");
    }

    private async Task<bool> ValidateGitlabToken(string token)
    {
        var request = new RestRequest("user");
        request.AddHeader("PRIVATE-TOKEN", token);
        var fullURL = _client.BuildUri(request);
        Log.Debug($"Validating token at URL: {fullURL}");
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

    private async Task CreateBobBuilderUser()
    {
        const string username = "b.builder";
        const string name = "Bob Builder";
        const string email = "b.builder@CILab.local";
        const string password = "changeme123";

        Log.Information($"Creating GitLab user '{username}'...");

        // Check if user already exists
        var searchRequest = new RestRequest("users")
            .AddQueryParameter("username", username);

        var searchResponse = await _client.ExecuteGetAsync<GitlabUser[]>(searchRequest);

        if (searchResponse is { IsSuccessful: true, Data: not null } && searchResponse.Data.Length > 0)
        {
            Log.Information($"User '{username}' already exists");

            // Ensure password is in .env
            _envFileService.SaveOrUpdateEnvFile("GITLAB_BOB_PASSWORD", password);

            return;
        }

        // Create the user
        var createRequest = new RestRequest("users", Method.Post)
            .AddJsonBody(new
            {
                username = username,
                name = name,
                email = email,
                password = password,
                skip_confirmation = true
            });

        var createResponse = await _client.ExecutePostAsync<GitlabUser>(createRequest);

        if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
            && createResponse.Data is not null)
        {
            Log.Information($"User '{username}' created successfully");

            // Save password to .env
            _envFileService.SaveOrUpdateEnvFile("GITLAB_BOB_PASSWORD", password);
            Log.Information("User password saved to .env as GITLAB_BOB_PASSWORD");

            return;
        }

        Log.Error($"Failed to create user: {(int)createResponse.StatusCode} - {createResponse.Content}");
        throw new InvalidOperationException(
            $"Failed to create GitLab user '{username}': {(int)createResponse.StatusCode} {createResponse.StatusCode} - {createResponse.Content}");
    }

    private async Task CreateTestAccounts()
    {
        const string password = "changeme123";

        for (var i = 1; i <= 3; i++)
        {
            var username = $"test{i}";
            var name = $"Test Account {i}";
            var email = $"test{i}@CILab.local";

            Log.Information($"Creating GitLab user '{username}'...");

            // Check if user already exists
            var searchRequest = new RestRequest("users")
                .AddQueryParameter("username", username);

            var searchResponse = await _client.ExecuteGetAsync<GitlabUser[]>(searchRequest);

            if (searchResponse is { IsSuccessful: true, Data: not null } && searchResponse.Data.Length > 0)
            {
                Log.Information($"User '{username}' already exists");
                continue;
            }

            // Create the user
            var createRequest = new RestRequest("users", Method.Post)
                .AddJsonBody(new
                {
                    username = username,
                    name = name,
                    email = email,
                    password = password,
                    skip_confirmation = true
                });

            var createResponse = await _client.ExecutePostAsync<GitlabUser>(createRequest);

            if (createResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
                && createResponse.Data is not null)
            {
                Log.Information($"User '{username}' created successfully");
            }
            else
            {
                Log.Error($"Failed to create user '{username}': {(int)createResponse.StatusCode} - {createResponse.Content}");
                throw new InvalidOperationException(
                    $"Failed to create GitLab user '{username}': {(int)createResponse.StatusCode} {createResponse.StatusCode} - {createResponse.Content}");
            }
        }
    }
}
