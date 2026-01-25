using Bootstrap.Entities.TeamCity;
using Bootstrap.Services.Utilities;
using Serilog;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Bootstrap.Services.TeamCity;

public class TeamCityBootstrapService
{
    private readonly PlaywrightService _browserService;

    private readonly HttpClient _client;

    private readonly EnvService _envService;

    private readonly string _password;

    private readonly string _teamcityUrl;

    private readonly string _username;

    public TeamCityBootstrapService(
        PlaywrightService browserService,
        EnvService envService,
        string teamcityUrl,
        HttpClient client,
        string username,
        string password)
    {
        _browserService = browserService;
        _envService = envService;
        _teamcityUrl = teamcityUrl;
        _client = client;
        _username = username;
        _password = password;
    }

    public async Task<bool> Execute()
    {
        Log.Information("Starting automated TeamCity initial setup");
        var screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "data", "screenshots");
        if (!await _browserService.Initialize(screenshotDir))
        {
            return false;
        }

        try
        {
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
        catch (Exception ex)
        {
            Log.Error($"TeamCity automated setup failed: {ex.Message}");
            Log.Error($"Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    private async Task<bool> IsInMaintenanceErrorState()
    {
        try
        {
            var pageContent = await _browserService.GetPageContent();
            if (pageContent.Contains("TeamCity server requires technical maintenance"))
            //&& pageContent.Contains("already logged in"))
            {
                Log.Error("TeamCity server is in maintenance mode");
                await _browserService.TakeScreenshot("error_maintenance_mode");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not check for maintenance message: {ex.Message}");
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

        if (await loginUsernameField.CountAsync() > 0
            && await loginPasswordField.CountAsync() > 0
            && await loginButton.CountAsync() > 0)
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

        if (await proceedButton.CountAsync() > 0)
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

            if (await dbProceedButton.CountAsync() > 0)
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

        if (await acceptCheckbox.CountAsync() == 0)
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

        if (await continueButton.CountAsync() == 0)
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

        if (!await _browserService.WaitForTextToDisappear(
                "License Agreement for JetBrains",
                30))
        {
            await _browserService.TakeScreenshot("error_license_still_present");
            return false;
        }

        var postLicenseText = await _browserService.GetPageContent();
        if (postLicenseText.Contains(
                "License Agreement for JetBrains",
                StringComparison.OrdinalIgnoreCase))
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

        if (await usernameField.CountAsync() == 0)
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

        if (await confirmPasswordField.CountAsync() > 0)
        {
            await PlaywrightService.FillFormField(
                confirmPasswordField,
                _password,
                "confirm password");

            await Task.Delay(300);
        }

        await _browserService.TakeScreenshot("09_admin_form_filled");

        var createAccountButton = _browserService.GetLocator(
            "button:has-text('Create Account'), input[value='Create Account'], button[type='submit']");

        if (await createAccountButton.CountAsync() > 0)
        {
            Log.Information("Submitting admin account creation...");
            await _browserService.ClickAndWait(
                createAccountButton,
                "Create Account button",
                5000);

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
            _envService.SaveOrUpdateEnvFile("TEAMCITY_TOKEN", token);
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
                var url = BuildApiUrl(endpoint);
                Log.Information($"Trying endpoint: {url}");

                // Try XML body first
                var token = await TryCreateTokenWithBody(
                    url,
                    "application/xml",
                    $"<token name=\"{WebUtility.HtmlEncode(tokenName)}\"/>");

                if (token != null)
                {
                    return token;
                }

                // Try JSON body
                Log.Information("Trying with JSON body...");
                token = await TryCreateTokenWithBody(
                    url,
                    "application/json",
                    JsonSerializer.Serialize(new { name = tokenName }));

                if (token != null)
                {
                    return token;
                }
            }

            return null;
        });
    }

    private async Task<string?> TryCreateTokenWithBody(
        string url,
        string contentType,
        string body)
    {
        try
        {
            var request = HttpRequestHelper.CreateWithBasicAuth(HttpMethod.Post, url, _username, _password);
            request.AddJsonAccept();
            request.Content = new StringContent(body, Encoding.UTF8, contentType);

            var response = await _client.SendAsync(request);
            var respText = await response.Content.ReadAsStringAsync();

            Log.Information($"Response status: {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Log.Information(
                    $"Response body: {respText.Substring(0, Math.Min(200, respText.Length))}");

                return null;
            }

            Log.Information("Success! Parsing response...");
            var token = ResponseParser.TryParseTokenFromResponse(respText);

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
        try
        {
            var apiUrl = BuildApiUrl("server");
            var request = HttpRequestHelper.CreateWithBearerAuth(HttpMethod.Get, apiUrl, token);

            var response = await _client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                Log.Information("Token authentication successful");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Validation error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> CreateProject(string token)
    {
        var apiUrl = BuildApiUrl("projects");

        Log.Information($"Creating TeamCity project 'Sample Project' via {apiUrl}");

        try
        {
            var xml = """<newProjectDescription name="Sample Project" id="SampleProject" />""";
            var request = HttpRequestHelper.CreateWithBearerAuth(HttpMethod.Post, apiUrl, token);
            request.AddJsonAccept();
            request.SetXmlContent(xml);

            var response = await _client.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                Log.Information("TeamCity project created successfully");
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();
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
        catch (Exception ex)
        {
            Log.Error($"Failed to call TeamCity API: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> AuthorizeAgents(string token)
    {
        var apiUrl = BuildApiUrl("agents");

        try
        {
            var listRequest = HttpRequestHelper.CreateWithBearerAuth(
                HttpMethod.Get,
                $"{apiUrl}?locator=authorized:false",
                token);

            listRequest.AddJsonAccept();

            var listResponse = await _client.SendAsync(listRequest);
            if (!listResponse.IsSuccessStatusCode)
            {
                Log.Error($"Failed to get agents list: {(int)listResponse.StatusCode}");
                return false;
            }

            var agentsResponse = await listResponse.Content.ReadFromJsonAsync<TeamCityAgentsResponse>();
            if (agentsResponse?.Agent is null || agentsResponse.Agent.Length == 0)
            {
                Log.Information("No unauthorized agents found");
                return true;
            }

            var authorizedCount = 0;
            foreach (var agent in agentsResponse.Agent)
            {
                var agentId = agent.Id;
                var agentName = agent.Name;

                Log.Information($"Authorizing agent: {agentName} (ID: {agentId})");

                var authRequest = HttpRequestHelper.CreateWithBearerAuth(
                    HttpMethod.Put,
                    $"{apiUrl}/id:{agentId}/authorized",
                    token);

                authRequest.Content = new StringContent("true", Encoding.UTF8, "text/plain");

                var authResponse = await _client.SendAsync(authRequest);
                if (authResponse.IsSuccessStatusCode)
                {
                    Log.Information($"Agent {agentName} authorized");
                    authorizedCount++;

                    var poolApiUrl = BuildApiUrl("agentPools/id:0/agents");
                    var poolRequest = HttpRequestHelper.CreateWithBearerAuth(
                        HttpMethod.Post,
                        poolApiUrl,
                        token);

                    poolRequest.SetXmlContent($"<agent id=\"{agentId}\" />");

                    var poolResponse = await _client.SendAsync(poolRequest);
                    if (poolResponse.IsSuccessStatusCode)
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
                    Log.Warning(
                        $"Failed to authorize agent {agentName}: {(int)authResponse.StatusCode}");
                }
            }

            Log.Information($"Authorized {authorizedCount} agent(s)");
            return authorizedCount > 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to authorize agents: {ex.Message}");
            throw;
        }
    }

    public async Task<string?> EnsureValidToken()
    {
        var existingToken = Environment.GetEnvironmentVariable("TEAMCITY_TOKEN");
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
            try
            {
                var createdToken = await TryCreateTokenViaApi("bootstrap-automation");

                if (!string.IsNullOrEmpty(createdToken))
                {
                    _envService.SaveOrUpdateEnvFile("TEAMCITY_TOKEN", createdToken);
                    Log.Information("TeamCity token created via API and saved to .env");
                    return createdToken;
                }

                Log.Error(
                    "Could not create TeamCity token via API; cannot continue without TEAMCITY_TOKEN");

                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"TeamCity API token creation failed: {ex.Message}");
                return null;
            }
        }

        return existingToken;
    }

    private string BuildApiUrl(string endpoint)
    {
        return ApiUrlHelper.BuildUrl(_teamcityUrl, "app/rest", endpoint);
    }
}