using Bootstrap.Services.Utilities;
using Bootstrap.Entities.TeamCity;
using Serilog;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Bootstrap.Services.TeamCity;

public class TeamCityBootstrapService
{
    private readonly PlaywrightService _browserService;

    public TeamCityBootstrapService(PlaywrightService browserService)
    {
        _browserService = browserService;
    }

    public async Task<bool> Execute(
        HttpClient client,
        string teamcityUrl,
        string username,
        string password)
    {
        Log.Information("Starting automated TeamCity initial setup");
        var screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "data", "screenshots");
        if (!await _browserService.Initialize(screenshotDir))
        {
            return false;
        }

        try
        {
            await _browserService.Navigate(teamcityUrl);
            await Task.Delay(3000);
            await _browserService.TakeScreenshot("01_initial_page");

            if (await IsInMaintenanceErrorState())
            {
                return false;
            }

            if (!await IsAccountAlreadyCreated(username, password))
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

                await HandleAdminAccountCreation(username, password);
            }

            await HandleTokenCreation(teamcityUrl);

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

    private async Task<bool> IsAccountAlreadyCreated(string username, string password)
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
            await PlaywrightService.FillFormField(loginUsernameField, username, "username");
            await PlaywrightService.FillFormField(loginPasswordField, password, "password");
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

        var dbProceedButton =
            _browserService.GetLocator("button:has-text('Proceed'), input[value='Proceed']");

        const int maxWaitForDb = 180;
        for (var i = 0; i < maxWaitForDb; i++)
        {
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

    private async Task HandleAdminAccountCreation(string username, string password)
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
        await PlaywrightService.FillFormField(usernameField, username, "username");
        await Task.Delay(300);

        var passwordField = _browserService.GetLocator(
                "input[name='password'], input[id='password1'], input[type='password']")
            .First;

        await PlaywrightService.FillFormField(passwordField, password, "password");
        await Task.Delay(300);

        var confirmPasswordField = _browserService.GetLocator(
            "input[name='confirmPassword'], input[id='password2'], input[name='retypedPassword']");

        if (await confirmPasswordField.CountAsync() > 0)
        {
            await PlaywrightService.FillFormField(
                confirmPasswordField,
                password,
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

    private async Task<bool> HandleTokenCreation(string teamcityUrl)
    {
        Log.Information("Step 5: Creating access token (UI)");
        try
        {
            await _browserService.Navigate($"{teamcityUrl}/profile.html?item=accessTokens");
            await Task.Delay(2000);
            await _browserService.TakeScreenshot("19_token_page");
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not navigate to token page: {ex.Message}");
            await _browserService.TakeScreenshot("19_token_page_error");
        }

        var existingToken = _browserService.GetLocator(
            "td:has-text('bootstrap-automation'), span:has-text('bootstrap-automation'), div:has-text('bootstrap-automation')");

        if (await existingToken.CountAsync() > 0)
        {
            Log.Information(
                "TeamCity token 'bootstrap-automation' already exists - skipping creation");

            await _browserService.TakeScreenshot("19_token_already_exists");
            return true;
        }

        var createTokenButton = _browserService.GetLocator(
            "button:has-text('Create access token'), "
            + "a:has-text('Create access token'), "
            + "input[value='Create access token'], "
            + "button:has-text('Create token'), "
            + "a:has-text('Create token'), "
            + "button.btn:has-text('Create'), "
            + "a.btn:has-text('Create')"
        );

        if (await createTokenButton.CountAsync() == 0)
        {
            Log.Warning("Could not find 'Create access token' button");
            var currentUrl = _browserService.Page.Url;
            Log.Information($"Current URL: {currentUrl}");
            return true;
        }

        Log.Information(
            $"Found create token button (matched {await createTokenButton.CountAsync()} elements)");

        await _browserService.ClickAndWait(createTokenButton, "Create token button", 20);
        await Task.Delay(1000);
        await _browserService.TakeScreenshot("20_token_creation_dialog");

        return await FillAndSubmitTokenForm();
    }

    private async Task<bool> FillAndSubmitTokenForm()
    {
        var tokenNameInput = _browserService.GetLocator(
            "input[name='tokenName'], input[id='tokenName'], input[name='name'], "
            + "input[placeholder*='name'], input[placeholder*='Token'], input[aria-label*='name'], textarea[name='name'], "
            + "input#input_accessTokenName, input[name='prop:accessTokenName']"
        );

        if (await tokenNameInput.CountAsync() == 0)
        {
            var dialog =
                _browserService.GetLocator("[role='dialog'], div.modal, div[aria-modal='true']");

            if (await dialog.CountAsync() > 0)
            {
                var innerInput = dialog.First.Locator("input, textarea, [contenteditable='true']");
                if (await innerInput.CountAsync() > 0)
                {
                    tokenNameInput = innerInput;
                }
            }
        }

        if (await tokenNameInput.CountAsync() == 0)
        {
            Log.Warning(
                "Could not find token name input field (tried multiple selectors)");

            return await TryFallbackTokenSubmit();
        }

        try
        {
            await PlaywrightService.FillFormField(
                tokenNameInput,
                "bootstrap-automation",
                "token name");

            await Task.Delay(500);
            await _browserService.TakeScreenshot("20b_token_name_filled");

            var createButton = _browserService.GetLocator(
                "button:has-text('Create'), input[value='Create'], button[type='submit'], button:has-text('Generate')");

            if (await createButton.CountAsync() == 0)
            {
                Log.Warning("Could not find Create/Generate button in dialog");
                return true;
            }

            await _browserService.ClickAndWait(
                createButton,
                "Create/Generate button",
                20);

            await Task.Delay(2000);
            await _browserService.TakeScreenshot("21_token_created");

            await ExtractAndSaveToken();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"Exception during token creation: {ex.Message}");
            return true;
        }
    }

    private async Task<bool> TryFallbackTokenSubmit()
    {
        try
        {
            var fallbackSubmit = _browserService.GetLocator(
                "button:has-text('Create'), button:has-text('Generate'), input[type='submit']");

            if (await fallbackSubmit.CountAsync() > 0)
            {
                Log.Information("Attempting fallback submit for token creation");
                await _browserService.ClickAndWait(
                    fallbackSubmit,
                    "fallback submit button",
                    20);

                await Task.Delay(1500);
                await _browserService.TakeScreenshot("21_token_created_fallback");
            }
        }
        catch { }

        return true;
    }

    private async Task ExtractAndSaveToken()
    {
        try
        {
            var createdTokenLocator = _browserService.GetLocator("#createdToken");
            var accessTokenRow = _browserService.GetLocator("#accessTokenValue");
            if (await accessTokenRow.CountAsync() > 0)
            {
                await PlaywrightService.WaitForElement(accessTokenRow);
            }

            if (await createdTokenLocator.CountAsync() == 0)
            {
                Log.Warning(
                    "Token created but '#createdToken' element not found");

                return;
            }

            var token = await PlaywrightService.GetTextContent(createdTokenLocator);

            if (string.IsNullOrWhiteSpace(token))
            {
                token = await PlaywrightService.GetAttribute(
                    createdTokenLocator,
                    "value");
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                var envPath = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "..",
                    "..",
                    ".env");

                var envFullPath = Path.GetFullPath(envPath);
                EnvHelper.SaveOrUpdateEnvFile(envFullPath, "TEAMCITY_TOKEN", token);
                Log.Information("TeamCity token created and saved to .env");
            }
            else
            {
                Log.Warning(
                    "Token created but '#createdToken' was empty");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(
                $"Exception while extracting created token: {ex.Message}");
        }
    }

    public async Task<string?> TryCreateTokenViaApi(
        HttpClient client,
        string teamcityUrl,
        string username,
        string password,
        string tokenName)
    {
        Log.Information(
            $"Attempting API token creation with username '{username}' and tokenName '{tokenName}'");

        var endpoints = new[]
        {
            $"users/username:{username}/tokens",
            $"users/{username}/tokens",
            "users/id:1/tokens",
            "users/current/tokens"
        };

        return await RetryHelper.Retry(async () =>
        {
            foreach (var endpoint in endpoints)
            {
                var url = BuildApiUrl(teamcityUrl, endpoint);
                Log.Information($"Trying endpoint: {url}");

                // Try XML body first
                var token = await TryCreateTokenWithBody(
                    client,
                    url,
                    username,
                    password,
                    "application/xml",
                    $"<token name=\"{WebUtility.HtmlEncode(tokenName)}\"/>");

                if (token != null)
                {
                    return token;
                }

                // Try JSON body
                Log.Information("Trying with JSON body...");
                token = await TryCreateTokenWithBody(
                    client,
                    url,
                    username,
                    password,
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
        HttpClient client,
        string url,
        string username,
        string password,
        string contentType,
        string body)
    {
        try
        {
            var request = HttpRequestHelper.CreateWithBasicAuth(HttpMethod.Post, url, username, password);
            request.AddJsonAccept();
            request.Content = new StringContent(body, Encoding.UTF8, contentType);

            var response = await client.SendAsync(request);
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

    public async Task<bool> ValidateTeamCityToken(HttpClient client, string teamcityUrl, string token)
    {
        try
        {
            var apiUrl = BuildApiUrl(teamcityUrl, "server");
            var request = HttpRequestHelper.CreateWithBearerAuth(HttpMethod.Get, apiUrl, token);

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var serverData = await response.Content.ReadAsStringAsync();
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

    public async Task<bool> CreateProject(HttpClient client, string teamcityUrl, string token)
    {
        var apiUrl = BuildApiUrl(teamcityUrl, "projects");

        Log.Information($"Creating TeamCity project 'Sample Project' via {apiUrl}");

        try
        {
            var xml = """<newProjectDescription name="Sample Project" id="SampleProject" />""";
            var request = HttpRequestHelper.CreateWithBearerAuth(HttpMethod.Post, apiUrl, token);
            request.AddJsonAccept();
            request.SetXmlContent(xml);

            var response = await client.SendAsync(request);

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

    public async Task<bool> AuthorizeAgents(HttpClient client, string teamcityUrl, string token)
    {
        var apiUrl = BuildApiUrl(teamcityUrl, "agents");

        try
        {
            var listRequest = HttpRequestHelper.CreateWithBearerAuth(
                HttpMethod.Get,
                $"{apiUrl}?locator=authorized:false",
                token);

            listRequest.AddJsonAccept();

            var listResponse = await client.SendAsync(listRequest);
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
                var agentName = agent.Name ?? $"agent-{agentId}";

                Log.Information($"Authorizing agent: {agentName} (ID: {agentId})");

                var authRequest = HttpRequestHelper.CreateWithBearerAuth(
                    HttpMethod.Put,
                    $"{apiUrl}/id:{agentId}/authorized",
                    token);

                authRequest.Content = new StringContent("true", Encoding.UTF8, "text/plain");

                var authResponse = await client.SendAsync(authRequest);
                if (authResponse.IsSuccessStatusCode)
                {
                    Log.Information($"Agent {agentName} authorized");
                    authorizedCount++;

                    var poolApiUrl = BuildApiUrl(teamcityUrl, "agentPools/id:0/agents");
                    var poolRequest = HttpRequestHelper.CreateWithBearerAuth(
                        HttpMethod.Post,
                        poolApiUrl,
                        token);

                    poolRequest.SetXmlContent($"<agent id=\"{agentId}\" />");

                    var poolResponse = await client.SendAsync(poolRequest);
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

    private static string BuildApiUrl(string teamcityUrl, string endpoint)
    {
        return ApiUrlHelper.BuildUrl(teamcityUrl, "app/rest", endpoint);
    }
}
