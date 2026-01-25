using Bootstrap.Entities.TeamCity;
using Bootstrap.Services.Utilities;
using RestSharp;
using RestSharp.Authenticators;
using Serilog;
using System.Net;

namespace Bootstrap.Services.TeamCity;

public class TeamCityBootstrapService : IDisposable
{
    private readonly PlaywrightService _browserService;

    private readonly RestClient _client;

    private readonly EnvFileService _envFileService;

    private readonly string _password;

    private readonly string _teamcityUrl;

    private readonly string _username;

    public TeamCityBootstrapService(
        PlaywrightService browserService,
        EnvFileService envFileService,
        string teamcityUrl,
        string username,
        string password)
    {
        _browserService = browserService;
        _envFileService = envFileService;
        _teamcityUrl = teamcityUrl.TrimEnd('/');
        _username = username;
        _password = password;

        _client = new RestClient(
            new RestClientOptions($"{_teamcityUrl}/app/rest")
            {
                ThrowOnAnyError = false,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                Timeout = TimeSpan.FromSeconds(30),
                Authenticator = new HttpBasicAuthenticator(username, password)
            });
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<bool> Execute()
    {
        Log.Information("Starting automated TeamCity initial setup");
        var screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "data", "screenshots");
        if (!await _browserService.Initialize(screenshotDir))
        {
            return false;
        }

        await _browserService.Navigate(_teamcityUrl);
        await Task.Delay(3000);
        await _browserService.TakeScreenshot("01_initial_page");

        if (await IsInMaintenanceErrorState())
        {
            return false;
        }

        if (!await IsAccountAlreadyCreated())
        {
            await HandleDataDirectoryConfiguration();
            if (!await HandleDatabaseSetup())
            {
                Log.Error("Database setup did not complete in time");
                return false;
            }

            if (!await HandleLicenseAgreement())
            {
                return false;
            }

            await HandleAdminAccountCreation();
        }

        if (!await HandleTokenCreation())
        {
            return false;
        }

        await _browserService.TakeScreenshot("22_final_state");
        Log.Information("TeamCity automated setup completed successfully");
        return true;
    }

    private async Task<bool> IsInMaintenanceErrorState()
    {
        var pageContent = await _browserService.GetPageContent();
        if (pageContent.Contains("TeamCity server requires technical maintenance"))
        {
            Log.Error("TeamCity server is in maintenance mode");
            await _browserService.TakeScreenshot("error_maintenance_mode");
            return true;
        }

        return false;
    }

    private async Task<bool> IsAccountAlreadyCreated()
    {
        var pageContent = await _browserService.GetPageContent();
        if (!pageContent.Contains("Log in to TeamCity", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Log.Information("TeamCity appears to be already configured (login page detected)");
        await _browserService.TakeScreenshot("already_configured_login");

        Log.Information("Logging in with provided credentials...");
        var loginUsernameField = _browserService.GetLocator("input[id='username']");
        var loginPasswordField = _browserService.GetLocator("input[id='password']");
        var loginButton = _browserService.GetLocator("input[type='submit'][name='submitLogin']");

        if (await loginUsernameField.CountWithRetry() > 0
            && await loginPasswordField.CountWithRetry() > 0
            && await loginButton.CountWithRetry() > 0)
        {
            await PlaywrightService.FillFormField(loginUsernameField, _username, "username");
            await PlaywrightService.FillFormField(loginPasswordField, _password, "password");
            await _browserService.TakeScreenshot("login_form_filled");

            await _browserService.ClickAndWait(loginButton, "login button");
            await _browserService.TakeScreenshot("after_login");

            Log.Information("Successfully logged in to already-configured TeamCity");
            return true;
        }

        Log.Error("Login form fields not found despite detecting login page");
        throw new InvalidOperationException("Login form fields not found");
    }

    private async Task HandleDataDirectoryConfiguration()
    {
        Log.Information("Step 1: Checking for data directory configuration screen");
        await _browserService.TakeScreenshot("02_before_data_directory");

        var proceedButton = _browserService
            .GetLocator("button:has-text('Proceed'), input[value='Proceed']")
            .First;

        if (await proceedButton.CountWithRetry() > 0)
        {
            Log.Information("Found Proceed button, clicking...");
            await _browserService.ClickAndWait(proceedButton, "Proceed button", 3000);
            await _browserService.TakeScreenshot("03_after_data_directory");
            Log.Information("Data directory step completed");
        }
    }

    private async Task<bool> HandleDatabaseSetup()
    {
        Log.Information("Step 2: Checking for database setup screen");
        await _browserService.TakeScreenshot("04_before_database");

        const int maxWaitForDb = 180;
        for (var i = 0; i < maxWaitForDb; i++)
        {
            var dbProceedButton =
                _browserService.GetLocator("button:has-text('Proceed'), input[value='Proceed']");

            if (await dbProceedButton.CountWithRetry() > 0)
            {
                Log.Information("Database setup ready, clicking Proceed...");
                await _browserService.ClickAndWait(dbProceedButton, "Database Proceed button", 3000);

                if (!await _browserService.WaitForTextToDisappear("Creating a new database"))
                {
                    await _browserService.TakeScreenshot("error_database_creation_still_present");
                    return false;
                }

                if (!await _browserService.WaitForTextToDisappear(
                        "Initializing TeamCity server components",
                        180))
                {
                    await _browserService.TakeScreenshot("error_database_initialization_still_present");
                    return false;
                }

                await _browserService.TakeScreenshot("05_after_database");
                Log.Information("Database setup completed");
                return true;
            }

            await Task.Delay(1000);
            if (i % 10 == 0 && i > 0)
            {
                Log.Information($"Still waiting for database initialization... ({i}s)");
            }
        }

        Log.Error("Timed out waiting for database initialization");
        await _browserService.TakeScreenshot("error_database_timeout");
        return false;
    }

    private async Task<bool> HandleLicenseAgreement()
    {
        Log.Information("Step 3: Checking for license agreement");
        await _browserService.TakeScreenshot("06_before_license");

        var pageText = await _browserService.GetPageContent();
        if (!pageText.Contains("License Agreement for JetBrains", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("License page not detected, skipping license acceptance step");
            return true;
        }

        var acceptCheckbox = _browserService.GetLocator(
            "input[type='checkbox'][name='accept'], input[id='accept'], input[name='acceptLicense']");

        if (await acceptCheckbox.CountWithRetry() == 0)
        {
            Log.Error("License checkbox not found");
            return false;
        }

        if (!await PlaywrightService.CheckCheckbox(acceptCheckbox, "license acceptance"))
        {
            return false;
        }

        await Task.Delay(1000);

        var continueButton = _browserService.GetLocator(
            "input[type='submit'][name='Continue'], input[type='submit'].submitButton, button:has-text('Continue'), input[value*='Continue']");

        if (await continueButton.CountWithRetry() == 0)
        {
            Log.Error("Continue button not found on license page");
            await _browserService.TakeScreenshot("error_license_no_continue");
            return false;
        }

        Log.Information("Waiting for Continue button to be enabled...");
        await PlaywrightService.WaitForElement(continueButton);

        Log.Information("Clicking Continue after license acceptance...");
        await _browserService.ClickAndWait(continueButton, "Continue button", 3000);
        await _browserService.TakeScreenshot("07_after_license");

        if (!await _browserService.WaitForTextToDisappear("License Agreement for JetBrains", 30))
        {
            await _browserService.TakeScreenshot("error_license_still_present");
            return false;
        }

        var postLicenseText = await _browserService.GetPageContent();
        if (postLicenseText.Contains("License Agreement for JetBrains", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error("License acceptance did not complete successfully");
            await _browserService.TakeScreenshot("error_license_still_present");
            return false;
        }

        Log.Information("License accepted");
        return true;
    }

    private async Task HandleAdminAccountCreation()
    {
        Log.Information("Step 4: Checking for admin account creation");
        await _browserService.TakeScreenshot("08_before_admin_creation");

        var usernameField =
            _browserService.GetLocator("input[name='username'], input[id='input_teamcityUsername']");

        if (await usernameField.CountWithRetry() == 0)
        {
            Log.Information("Admin account may already exist, continuing...");
            return;
        }

        Log.Information("Found admin creation form, filling in details...");
        await PlaywrightService.FillFormField(usernameField, _username, "username");
        await Task.Delay(300);

        var passwordField = _browserService.GetLocator(
                "input[name='password'], input[id='password1'], input[type='password']")
            .First;

        await PlaywrightService.FillFormField(passwordField, _password, "password");
        await Task.Delay(300);

        var confirmPasswordField = _browserService.GetLocator(
            "input[name='confirmPassword'], input[id='password2'], input[name='retypedPassword']");

        if (await confirmPasswordField.CountWithRetry() > 0)
        {
            await PlaywrightService.FillFormField(confirmPasswordField, _password, "confirm password");
            await Task.Delay(300);
        }

        await _browserService.TakeScreenshot("09_admin_form_filled");

        var createAccountButton = _browserService.GetLocator(
            "button:has-text('Create Account'), input[value='Create Account'], button[type='submit']");

        if (await createAccountButton.CountWithRetry() > 0)
        {
            Log.Information("Submitting admin account creation...");
            await _browserService.ClickAndWait(createAccountButton, "Create Account button", 5000);
            await _browserService.TakeScreenshot("10_after_admin_creation");
            Log.Information("Admin account created successfully");
        }
    }

    private async Task<bool> HandleTokenCreation()
    {
        Log.Information("Step 5: Creating access token (API)");

        const string tokenName = "bootstrap-automation";
        var token = await TryCreateTokenViaApi(tokenName);

        if (token != null)
        {
            Log.Information($"Successfully created token '{tokenName}' via API");
            _envFileService.SaveOrUpdateEnvFile("TEAMCITY_TOKEN", token);
            return true;
        }

        Log.Warning("Failed to create token via API");
        return false;
    }

    public async Task<string?> TryCreateTokenViaApi(string tokenName)
    {
        Log.Information(
            $"Attempting API token creation with username '{_username}' and tokenName '{tokenName}'");

        var endpoints = new[]
        {
            $"users/username:{_username}/tokens",
            $"users/{_username}/tokens",
            "users/id:1/tokens",
            "users/current/tokens"
        };

        return await RetryHelper.Retry(async () =>
        {
            foreach (var endpoint in endpoints)
            {
                // First check if token with this name already exists
                var tokenExists = await CheckTokenExists(endpoint, tokenName);
                if (tokenExists)
                {
                    Log.Information($"Token '{tokenName}' already exists - checking if we have it in .env");

                    // Token exists in TeamCity but we can't retrieve its value via API
                    // Check if we have it stored in the environment
                    var existingToken = _envFileService.GetValue("TEAMCITY_TOKEN");
                    if (!string.IsNullOrWhiteSpace(existingToken))
                    {
                        // Validate that this token actually works
                        if (await ValidateTeamCityToken(existingToken))
                        {
                            Log.Information("Using existing TEAMCITY_TOKEN from environment");
                            return existingToken;
                        }
                    }

                    Log.Warning($"Token '{tokenName}' exists but we don't have a valid value stored");
                    Log.Warning(
                        "Cannot retrieve existing token value via API - TeamCity only returns values on creation");

                    return null;
                }

                Log.Information(
                    $"No existing token found, creating new one at: {_teamcityUrl}/app/rest/{endpoint}");

                var token = await TryCreateTokenWithJson(endpoint, tokenName);
                if (token != null)
                {
                    return token;
                }
            }

            return null;
        });
    }

    private async Task<bool> CheckTokenExists(string endpoint, string tokenName)
    {
        try
        {
            var request = new RestRequest(endpoint)
                .AddHeader("Accept", "application/json");

            var response = await _client.ExecuteGetAsync<TeamCityTokensResponse>(request);

            if (!response.IsSuccessful || response.Data?.Token == null)
            {
                return false;
            }

            return response.Data.Token.Any(t =>
                string.Equals(t?.Name, tokenName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> TryCreateTokenWithJson(string endpoint, string tokenName)
    {
        try
        {
            var request = new RestRequest(endpoint, Method.Post)
                .AddHeader("Accept", "application/json")
                .AddJsonBody(new { name = tokenName });

            var response = await _client.ExecuteAsync(request);
            Log.Information($"Response status: {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessful)
            {
                Log.Information(
                    $"Response body: {response.Content?[..Math.Min(200, response.Content?.Length ?? 0)]}");

                return null;
            }

            Log.Information("Success! Parsing response...");
            var token = ResponseParser.TryParseTokenFromResponse(response.Content ?? "");

            if (token != null)
            {
                Log.Information($"Token extracted (length: {token.Length})");
                return token;
            }

            Log.Warning("Success response but couldn't extract token");
            return null;
        }
        catch (Exception ex)
        {
            Log.Information($"Exception: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> ValidateTeamCityToken(string token)
    {
        var request = new RestRequest("server")
            .AddHeader("Authorization", $"Bearer {token}");

        var response = await _client.ExecuteGetAsync(request);
        if (response.IsSuccessful)
        {
            Log.Information("Token authentication successful");
            return true;
        }

        return false;
    }

    public async Task<bool> CreateProject(string token)
    {
        Log.Information("Creating TeamCity project 'Sample Project'");
        var request = new RestRequest("projects", Method.Post)
            .AddHeader("Authorization", $"Bearer {token}")
            .AddHeader("Accept", "application/json")
            .AddStringBody(
                """<newProjectDescription name="Sample Project" id="SampleProject" />""",
                ContentType.Xml);

        var response = await _client.ExecuteAsync(request);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            Log.Information("TeamCity project created successfully");
            return true;
        }

        var body = response.Content ?? "";
        if (response.StatusCode == HttpStatusCode.Conflict
            || body.Contains("DuplicateProjectNameException")
            || body.Contains("already exists"))
        {
            Log.Information("TeamCity project already exists");
            return true;
        }

        Log.Error($"TeamCity API error {(int)response.StatusCode}: {body}");
        return false;
    }

    public async Task<bool> AuthorizeAgents(string token)
    {
        var listRequest = new RestRequest("agents")
            .AddHeader("Authorization", $"Bearer {token}")
            .AddQueryParameter("locator", "authorized:false");

        var listResponse = await _client.ExecuteGetAsync<TeamCityAgentsResponse>(listRequest);

        if (!listResponse.IsSuccessful)
        {
            Log.Error($"Failed to get agents list: {(int)listResponse.StatusCode}");
            return false;
        }

        if (listResponse.Data?.Agent is null || listResponse.Data.Agent.Length == 0)
        {
            Log.Information("No unauthorized agents found");
            return true;
        }

        foreach (var agent in listResponse.Data.Agent)
        {
            var agentId = agent.Id;
            var agentName = agent.Name;

            Log.Information($"Authorizing agent: {agentName} (ID: {agentId})");

            var authRequest = new RestRequest($"agents/id:{agentId}/authorized", Method.Put)
                .AddHeader("Authorization", $"Bearer {token}")
                .AddStringBody("true", ContentType.Plain);

            var authResponse = await _client.ExecuteAsync(authRequest);

            if (authResponse.IsSuccessful)
            {
                Log.Information($"Agent {agentName} authorized");

                var poolRequest = new RestRequest("agentPools/id:0/agents", Method.Post)
                    .AddHeader("Authorization", $"Bearer {token}")
                    .AddStringBody($"<agent id=\"{agentId}\" />", ContentType.Xml);

                var poolResponse = await _client.ExecuteAsync(poolRequest);

                if (poolResponse.IsSuccessful)
                {
                    Log.Information($"Agent {agentName} added to default pool");
                }
                else
                {
                    Log.Warning(
                        $"Could not add agent {agentName} to pool: {(int)poolResponse.StatusCode}");
                }
            }
            else
            {
                Log.Warning($"Failed to authorize agent {agentName}: {(int)authResponse.StatusCode}");
                return false;
            }
        }

        return true;
    }

    public async Task<string?> EnsureValidToken()
    {
        var existingToken = _envFileService.GetValue("TEAMCITY_TOKEN");
        var needCreateToken = string.IsNullOrEmpty(existingToken);

        if (!needCreateToken && existingToken != null)
        {
            Log.Information("Validating existing TEAMCITY_TOKEN...");
            try
            {
                var valid = await ValidateTeamCityToken(existingToken);

                if (!valid)
                {
                    Log.Information(
                        "Existing TEAMCITY_TOKEN is invalid or insufficient permissions; will attempt to create a new token via API");

                    needCreateToken = true;
                }
                else
                {
                    Log.Information("Existing TEAMCITY_TOKEN is valid");
                    return existingToken;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error validating existing TEAMCITY_TOKEN: {ex.Message}");
                needCreateToken = true;
            }
        }

        if (needCreateToken)
        {
            Log.Information("Attempting to create TeamCity token via REST API...");
            var createdToken = await TryCreateTokenViaApi("bootstrap-automation");

            if (!string.IsNullOrEmpty(createdToken))
            {
                _envFileService.SaveOrUpdateEnvFile("TEAMCITY_TOKEN", createdToken);
                Log.Information("TeamCity token created via API and saved to .env");
                return createdToken;
            }

            Log.Error("Could not create TeamCity token via API; cannot continue without TEAMCITY_TOKEN");
            return null;
        }

        return existingToken;
    }
}