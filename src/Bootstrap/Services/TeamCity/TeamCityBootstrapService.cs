using Bootstrap.Services.Utilities;
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

    private static string BuildApiUrl(string teamcityUrl, string endpoint)
    {
        return ApiUrlHelper.BuildUrl(teamcityUrl, "app/rest", endpoint);
    }

    public async Task<bool> Execute(
        HttpClient client,
        string teamcityUrl,
        string username,
        string password)
    {
        Logging.Log("Starting automated TeamCity initial setup using Playwright...");
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

            // Check for maintenance/admin limit error early
            if (await CheckForMaintenanceError())
            {
                return false;
            }

            // Check if TeamCity is already configured (login page present)
            var pageContent = await _browserService.GetPageContent();
            if (pageContent.Contains("Log in to TeamCity", StringComparison.OrdinalIgnoreCase))
            {
                Logging.Log("TeamCity appears to be already configured (login page detected)");
                await _browserService.TakeScreenshot("already_configured_login");

                // Log in with the provided credentials
                Logging.LogInfo("Logging in with provided credentials...", 1);
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

                    Logging.LogSuccess("Successfully logged in to already-configured TeamCity", 1);

                    // Skip setup steps and go directly to token verification/creation
                    // The token check logic will happen after this method returns
                    return true;
                }

                Logging.LogError("Login form fields not found despite detecting login page");
                return false;
            }

            // The rest of the original flow is preserved here; many UI interactions
            // and token extraction attempts are performed. For brevity the implementation
            // matches the previous Program.cs logic but uses Console logging and
            // EnvHelper for environment updates.

            // Step 1: Data Directory - Click Proceed
            Logging.Log("Step 1: Checking for data directory configuration screen");
            await _browserService.TakeScreenshot("02_before_data_directory");

            var proceedButton = _browserService
                .GetLocator("button:has-text('Proceed'), input[value='Proceed']")
                .First;

            if (await proceedButton.CountAsync() > 0)
            {
                Logging.LogInfo("Found Proceed button, clicking...", 1);
                await _browserService.ClickAndWait(proceedButton, "Proceed button", 3000);
                await _browserService.TakeScreenshot("03_after_data_directory");
                Logging.LogInfo("Data directory step completed", 1);
            }

            // Step 2: Database Setup - Wait and Proceed
            Logging.Log("Step 2: Checking for database setup screen");
            await Task.Delay(2000);
            await _browserService.TakeScreenshot("04_before_database");

            var dbProceedButton =
                _browserService.GetLocator("button:has-text('Proceed'), input[value='Proceed']");

            const int maxWaitForDb = 60; // Wait up to 60 seconds for DB initialization
            for (var i = 0; i < maxWaitForDb; i++)
            {
                if (await dbProceedButton.CountAsync() > 0)
                {
                    Logging.LogInfo("Database setup ready, clicking Proceed...", 1);
                    await _browserService.ClickAndWait(dbProceedButton, "Database Proceed button", 3000);

                    // Wait for TeamCity database initialization message to clear
                    await _browserService.WaitForTextToDisappear("Creating a new database");

                    // Also wait for server components initialization message to clear if present
                    await _browserService.WaitForTextToDisappear(
                        "Initializing TeamCity server components",
                        180);

                    await _browserService.TakeScreenshot("05_after_database");
                    Logging.LogInfo("Database setup completed", 1);
                    break;
                }

                await Task.Delay(1000);
                if (i % 10 == 0 && i > 0)
                {
                    Logging.LogInfo($"Still waiting for database initialization... ({i}s)", 1);
                }
            }

            // Step 3: License Agreement - Accept
            Logging.Log("Step 3: Checking for license agreement");
            await Task.Delay(2000);
            await _browserService.TakeScreenshot("06_before_license");

            var pageText = await _browserService.GetPageContent();
            if (pageText.IndexOf("License Agreement for JetBrains", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var acceptCheckbox = _browserService.GetLocator(
                    "input[type='checkbox'][name='accept'], input[id='accept'], input[name='acceptLicense']");

                if (await acceptCheckbox.CountAsync() > 0)
                {
                    if (await PlaywrightService.CheckCheckbox(acceptCheckbox, "license acceptance"))
                    {
                        await Task.Delay(1000); // Wait for JavaScript to enable the button

                        var continueButton = _browserService.GetLocator(
                            "input[type='submit'][name='Continue'], input[type='submit'].submitButton, button:has-text('Continue'), input[value*='Continue']");

                        if (await continueButton.CountAsync() == 0)
                        {
                            Logging.LogError("Continue button not found on license page");
                            await _browserService.TakeScreenshot("error_license_no_continue");
                            return false;
                        }

                        // Wait for button to be enabled (JavaScript enables it after checkbox is checked)
                        Logging.LogInfo("Waiting for Continue button to be enabled...", 1);
                        await PlaywrightService.WaitForElement(continueButton);

                        Logging.LogInfo("Clicking Continue after license acceptance...", 1);
                        await _browserService.ClickAndWait(continueButton, "Continue button", 3000);
                        await _browserService.TakeScreenshot("07_after_license");

                        // Ensure license text disappeared
                        await _browserService.WaitForTextToDisappear(
                            "License Agreement for JetBrains",
                            30);

                        var postLicenseText = await _browserService.GetPageContent();
                        if (postLicenseText.IndexOf(
                                "License Agreement for JetBrains",
                                StringComparison.OrdinalIgnoreCase)
                            >= 0)
                        {
                            Logging.LogError("License acceptance did not complete successfully");
                            await _browserService.TakeScreenshot("error_license_still_present");
                            return false;
                        }

                        Logging.LogInfo("License accepted", 1);
                    }
                }
                else
                {
                    Logging.LogInfo("License checkbox not found, skipping license acceptance step", 1);
                }
            }
            else
            {
                Logging.LogInfo("License page not detected, skipping license acceptance step", 1);
            }

            // Step 4: Create Administrator Account
            Logging.Log("Step 4: Checking for admin account creation");
            await Task.Delay(2000);
            await _browserService.TakeScreenshot("08_before_admin_creation");

            var usernameField =
                _browserService.GetLocator("input[name='username'], input[id='input_teamcityUsername']");

            if (await usernameField.CountAsync() > 0)
            {
                Logging.LogInfo("Found admin creation form, filling in details...", 1);
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
                    Logging.LogInfo("Submitting admin account creation...", 1);
                    await _browserService.ClickAndWait(
                        createAccountButton,
                        "Create Account button",
                        5000);

                    await _browserService.TakeScreenshot("10_after_admin_creation");
                    Logging.LogSuccess("Admin account created successfully", 1);
                }
            }
            else
            {
                Logging.LogInfo("Admin account may already exist, continuing...", 1);
            }

            // Navigate to token creation page and attempt to create/access token
            Logging.Log("Step 5: Creating access token (UI)");
            try
            {
                await _browserService.Navigate($"{teamcityUrl}/profile.html?item=accessTokens");
                await Task.Delay(2000);
                await _browserService.TakeScreenshot("19_token_page");
            }
            catch (Exception ex)
            {
                Logging.LogWarning($"Could not navigate to token page: {ex.Message}", 1);
                await _browserService.TakeScreenshot("19_token_page_error");
            }

            // Try to find existing token
            var existingToken = _browserService.GetLocator(
                "td:has-text('bootstrap-automation'), span:has-text('bootstrap-automation'), div:has-text('bootstrap-automation')");

            if (await existingToken.CountAsync() > 0)
            {
                Logging.LogSuccess(
                    "TeamCity token 'bootstrap-automation' already exists - skipping creation",
                    1);

                await _browserService.TakeScreenshot("19_token_already_exists");
                return true;
            }

            // Try to create token using UI
            var createTokenButton = _browserService.GetLocator(
                "button:has-text('Create access token'), "
                + "a:has-text('Create access token'), "
                + "input[value='Create access token'], "
                + "button:has-text('Create token'), "
                + "a:has-text('Create token'), "
                + "button.btn:has-text('Create'), "
                + "a.btn:has-text('Create')"
            );

            if (await createTokenButton.CountAsync() > 0)
            {
                Logging.LogInfo(
                    $"Found create token button (matched {await createTokenButton.CountAsync()} elements)",
                    1);

                await _browserService.ClickAndWait(createTokenButton, "Create token button", 20);
                await Task.Delay(1000);
                await _browserService.TakeScreenshot("20_token_creation_dialog");

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

                if (await tokenNameInput.CountAsync() > 0)
                {
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

                        if (await createButton.CountAsync() > 0)
                        {
                            await _browserService.ClickAndWait(
                                createButton,
                                "Create/Generate button",
                                20);

                            await Task.Delay(2000);
                            await _browserService.TakeScreenshot("21_token_created");
                            // Wait for the created token element to appear (#createdToken) or the accessTokenValue row to be shown
                            try
                            {
                                var createdTokenLocator = _browserService.GetLocator("#createdToken");
                                var accessTokenRow = _browserService.GetLocator("#accessTokenValue");
                                if (await accessTokenRow.CountAsync() > 0)
                                {
                                    await PlaywrightService.WaitForElement(accessTokenRow);
                                }

                                if (await createdTokenLocator.CountAsync() > 0)
                                {
                                    var token =
                                        await PlaywrightService.GetTextContent(createdTokenLocator);

                                    if (string.IsNullOrWhiteSpace(token))
                                    {
                                        // Sometimes the token may be in a child or as a value attribute
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
                                        Logging.LogSuccess("TeamCity token created and saved to .env", 3);
                                    }
                                    else
                                    {
                                        Logging.LogWarning(
                                            "Token created but '#createdToken' was empty",
                                            3);
                                    }
                                }
                                else
                                {
                                    Logging.LogWarning(
                                        "Token created but '#createdToken' element not found",
                                        3);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logging.LogWarning(
                                    $"Exception while extracting created token: {ex.Message}",
                                    3);
                            }
                        }
                        else
                        {
                            Logging.LogWarning("Could not find Create/Generate button in dialog", 2);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.LogWarning($"Exception during token creation: {ex.Message}", 2);
                    }
                }
                else
                {
                    Logging.LogWarning(
                        "Could not find token name input field (tried multiple selectors)",
                        2);

                    try
                    {
                        var fallbackSubmit = _browserService.GetLocator(
                            "button:has-text('Create'), button:has-text('Generate'), input[type='submit']");

                        if (await fallbackSubmit.CountAsync() > 0)
                        {
                            Logging.LogInfo("Attempting fallback submit for token creation", 2);
                            await _browserService.ClickAndWait(
                                fallbackSubmit,
                                "fallback submit button",
                                20);

                            await Task.Delay(1500);
                            await _browserService.TakeScreenshot("21_token_created_fallback");
                        }
                    }
                    catch { }
                }
            }
            else
            {
                Logging.LogWarning("Could not find 'Create access token' button", 1);
                var currentUrl = _browserService.Page.Url;
                Logging.LogInfo($"Current URL: {currentUrl}", 1);
            }

            await _browserService.TakeScreenshot("22_final_state");
            Logging.LogSuccess("TeamCity automated setup completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logging.LogError($"TeamCity automated setup failed: {ex.Message}");
            Logging.LogError($"Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    private async Task<bool> CheckForMaintenanceError()
    {
        try
        {
            var pageContent = await _browserService.GetPageContent();
            if (pageContent.Contains("TeamCity server requires technical maintenance")
                && pageContent.Contains("already logged in"))
            {
                Logging.LogError("TeamCity server is in maintenance mode");
                await _browserService.TakeScreenshot("error_maintenance_mode");
                return true;
            }
        }
        catch (Exception ex)
        {
            Logging.LogWarning($"Could not check for maintenance message: {ex.Message}");
        }

        return false;
    }

    public async Task<string?> TryCreateTokenViaApi(
        HttpClient client,
        string teamcityUrl,
        string username,
        string password,
        string tokenName)
    {
        Logging.LogInfo(
            $"Attempting API token creation with username '{username}' and tokenName '{tokenName}'",
            1);

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
                Logging.LogInfo($"Trying endpoint: {url}", 2);

                // Try XML body first
                var token = await TryCreateTokenWithBody(
                    client,
                    url,
                    username,
                    password,
                    tokenName,
                    "application/xml",
                    $"<token name=\"{WebUtility.HtmlEncode(tokenName)}\"/>");

                if (token != null)
                {
                    return token;
                }

                // Try JSON body
                Logging.LogInfo("Trying with JSON body...", 2);
                token = await TryCreateTokenWithBody(
                    client,
                    url,
                    username,
                    password,
                    tokenName,
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
        string tokenName,
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

            Logging.LogInfo($"Response status: {(int)response.StatusCode} {response.StatusCode}", 3);

            if (!response.IsSuccessStatusCode)
            {
                Logging.LogInfo(
                    $"Response body: {respText.Substring(0, Math.Min(200, respText.Length))}",
                    3);

                return null;
            }

            Logging.LogSuccess("Success! Parsing response...", 3);
            var token = ResponseParser.TryParseTokenFromResponse(respText);

            if (token != null)
            {
                Logging.LogSuccess($"Token extracted (length: {token.Length})", 3);
                return token;
            }

            Logging.LogWarning("Success response but couldn't extract token", 3);
            return null;
        }
        catch (Exception ex)
        {
            Logging.LogInfo($"Exception: {ex.Message}", 3);
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
                Logging.LogInfo("Token authentication successful", 1);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logging.LogError($"Validation error: {ex.Message}", 1);
            return false;
        }
    }

    public async Task<bool> CreateProject(HttpClient client, string teamcityUrl, string token)
    {
        var apiUrl = BuildApiUrl(teamcityUrl, "projects");

        Logging.Log($"Creating TeamCity project 'Sample Project' via {apiUrl}");

        try
        {
            var xml = """<newProjectDescription name="Sample Project" id="SampleProject" />""";
            var request = HttpRequestHelper.CreateWithBearerAuth(HttpMethod.Post, apiUrl, token);
            request.AddJsonAccept();
            request.SetXmlContent(xml);

            var response = await client.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                Logging.LogSuccess("TeamCity project created successfully");
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.Conflict
                || body.Contains("DuplicateProjectNameException")
                || body.Contains("already exists"))
            {
                Logging.Log("TeamCity project already exists");
                return true;
            }

            Logging.LogError($"TeamCity API error {(int)response.StatusCode}: {body}");
            return false;
        }
        catch (Exception ex)
        {
            Logging.LogError($"Failed to call TeamCity API: {ex.Message}");
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
                Logging.LogError($"Failed to get agents list: {(int)listResponse.StatusCode}");
                return false;
            }

            var agentsData = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
            if (!agentsData.TryGetProperty("agent", out var agents))
            {
                Logging.Log("No unauthorized agents found");
                return true;
            }

            var authorizedCount = 0;
            foreach (var agent in agents.EnumerateArray())
            {
                var agentId = agent.GetProperty("id").GetInt32();
                var agentName = agent.TryGetProperty("name", out var name)
                    ? name.GetString()
                    : $"agent-{agentId}";

                Logging.Log($"Authorizing agent: {agentName} (ID: {agentId})");

                var authRequest = HttpRequestHelper.CreateWithBearerAuth(
                    HttpMethod.Put,
                    $"{apiUrl}/id:{agentId}/authorized",
                    token);

                authRequest.Content = new StringContent("true", Encoding.UTF8, "text/plain");

                var authResponse = await client.SendAsync(authRequest);
                if (authResponse.IsSuccessStatusCode)
                {
                    Logging.LogSuccess($"Agent {agentName} authorized", 1);
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
                        Logging.LogSuccess($"Agent {agentName} added to default pool", 1);
                    }
                    else
                    {
                        Logging.LogWarning(
                            $"Could not add agent {agentName} to pool: {(int)poolResponse.StatusCode}",
                            1);
                    }
                }
                else
                {
                    Logging.LogWarning(
                        $"Failed to authorize agent {agentName}: {(int)authResponse.StatusCode}",
                        1);
                }
            }

            Logging.Log($"Authorized {authorizedCount} agent(s)");
            return authorizedCount > 0;
        }
        catch (Exception ex)
        {
            Logging.LogError($"Failed to authorize agents: {ex.Message}");
            return false;
        }
    }
}