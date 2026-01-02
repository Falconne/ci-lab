using Bootstrap.Services.Utilities;
using Microsoft.Playwright;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Bootstrap.Services.TeamCity;

public class TeamCityService
{
    public async Task<bool> AutomateTeamCitySetupAsync(
        HttpClient client,
        string teamcityUrl,
        string username,
        string password)
    {
        var screenshotDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "data", "screenshots");
        var screenshotCounter = 0;

        if (Directory.Exists(screenshotDir))
        {
            Directory.Delete(screenshotDir, true);
        }

        Directory.CreateDirectory(screenshotDir);
        LogHelper.Log($"Screenshot directory created: {screenshotDir}");

        async Task TakeScreenshot(IPage page, string description)
        {
            try
            {
                screenshotCounter++;
                var timestamp = DateTime.UtcNow.ToString("HHmmss");
                var filename = $"{screenshotCounter:D3}_{timestamp}_{description}.png";
                var path = Path.Combine(screenshotDir, filename);
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
                LogHelper.LogInfo($"📸 Screenshot saved: {filename}", 1);
            }
            catch (Exception ex)
            {
                LogHelper.LogWarning($"Could not save screenshot: {ex.Message}", 1);
            }
        }

        async Task<bool> CheckForMaintenanceErrorAsync(IPage page)
        {
            try
            {
                var pageContent = await page.ContentAsync();
                if (pageContent.Contains("TeamCity server requires technical maintenance")
                    && pageContent.Contains("already logged in"))
                {
                    LogHelper.LogError("TeamCity server is in maintenance mode");
                    await TakeScreenshot(page, "error_maintenance_mode");
                    return true; // Error detected
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogWarning($"Could not check for maintenance message: {ex.Message}");
            }

            return false; // No error
        }

        async Task WaitForTextToDisappearAsync(
            IPage page,
            string text,
            int timeoutSeconds = 120,
            int pollMs = 1000)
        {
            var found = false;
            for (var s = 0; s < timeoutSeconds; s++)
            {
                try
                {
                    var pageContent = await page.ContentAsync();
                    if (pageContent != null
                        && pageContent.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = true;
                        if (s % 5 == 0)
                        {
                            LogHelper.LogInfo($"Waiting for '{text}' to disappear... waited {s}s", 1);
                        }

                        await Task.Delay(pollMs);
                        continue;
                    }

                    break;
                }
                catch (Exception ex)
                {
                    LogHelper.LogWarning(
                        $"Warning reading page content while waiting for '{text}': {ex.Message}",
                        1);

                    await Task.Delay(pollMs);
                }
            }

            if (found)
            {
                LogHelper.LogInfo($"'{text}' no longer present (or timed out waiting)", 1);
            }
        }

        try
        {
            LogHelper.Log("Starting automated TeamCity initial setup using Playwright...");

            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (exitCode != 0)
            {
                LogHelper.LogWarning(
                    "Playwright browser installation returned non-zero exit code, continuing anyway...");
            }

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true, Timeout = 60000 });

            var context =
                await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(60000);

            LogHelper.Log($"Navigating to {teamcityUrl}");
            await page.GotoAsync(teamcityUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            await Task.Delay(3000);
            await TakeScreenshot(page, "01_initial_page");

            // Check for maintenance/admin limit error early
            if (await CheckForMaintenanceErrorAsync(page))
            {
                return false;
            }

            // Check if TeamCity is already configured (login page present)
            var pageContent = await page.ContentAsync();
            if (pageContent != null
                && pageContent.Contains("Log in to TeamCity", StringComparison.OrdinalIgnoreCase))
            {
                LogHelper.Log("TeamCity appears to be already configured (login page detected)");
                await TakeScreenshot(page, "already_configured_login");

                // Log in with the provided credentials
                LogHelper.LogInfo("Logging in with provided credentials...", 1);
                var loginUsernameField = page.Locator("input[id='username']");
                var loginPasswordField = page.Locator("input[id='password']");
                var loginButton = page.Locator("input[type='submit'][name='submitLogin']");

                if (await loginUsernameField.CountAsync() > 0
                    && await loginPasswordField.CountAsync() > 0
                    && await loginButton.CountAsync() > 0)
                {
                    await loginUsernameField.FillAsync(username);
                    await loginPasswordField.FillAsync(password);
                    await TakeScreenshot(page, "login_form_filled");

                    await loginButton.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await Task.Delay(2000);
                    await TakeScreenshot(page, "after_login");

                    LogHelper.LogSuccess("Successfully logged in to already-configured TeamCity", 1);

                    // Skip setup steps and go directly to token verification/creation
                    // The token check logic will happen after this method returns
                    return true;
                }

                LogHelper.LogError("Login form fields not found despite detecting login page");
                return false;
            }

            // The rest of the original flow is preserved here; many UI interactions
            // and token extraction attempts are performed. For brevity the implementation
            // matches the previous Program.cs logic but uses Console logging and
            // EnvHelper for environment updates.

            // Step 1: Data Directory - Click Proceed
            LogHelper.Log("Step 1: Checking for data directory configuration screen");
            await TakeScreenshot(page, "02_before_data_directory");

            var proceedButton = page.Locator("button:has-text('Proceed'), input[value='Proceed']").First;
            if (await proceedButton.CountAsync() > 0)
            {
                LogHelper.LogInfo("Found Proceed button, clicking...", 1);
                await proceedButton.ClickAsync();
                LogHelper.LogInfo("Waiting for navigation...", 1);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await Task.Delay(3000);
                await TakeScreenshot(page, "03_after_data_directory");
                LogHelper.LogInfo("Data directory step completed", 1);
            }

            // Step 2: Database Setup - Wait and Proceed
            LogHelper.Log("Step 2: Checking for database setup screen");
            await Task.Delay(2000);
            await TakeScreenshot(page, "04_before_database");

            var dbProceedButton = page.Locator("button:has-text('Proceed'), input[value='Proceed']");
            var maxWaitForDb = 60; // Wait up to 60 seconds for DB initialization
            for (var i = 0; i < maxWaitForDb; i++)
            {
                if (await dbProceedButton.CountAsync() > 0)
                {
                    LogHelper.LogInfo("Database setup ready, clicking Proceed...", 1);
                    await dbProceedButton.First.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await Task.Delay(3000);

                    // Wait for TeamCity database initialization message to clear
                    await WaitForTextToDisappearAsync(page, "Creating a new database");

                    // Also wait for server components initialization message to clear if present
                    await WaitForTextToDisappearAsync(page, "Initializing TeamCity server components", 180);

                    await TakeScreenshot(page, "05_after_database");
                    LogHelper.LogInfo("Database setup completed", 1);
                    break;
                }

                await Task.Delay(1000);
                if (i % 10 == 0 && i > 0)
                {
                    LogHelper.LogInfo($"Still waiting for database initialization... ({i}s)", 1);
                }
            }

            // Step 3: License Agreement - Accept
            LogHelper.Log("Step 3: Checking for license agreement");
            await Task.Delay(2000);
            await TakeScreenshot(page, "06_before_license");

            var pageText = await page.ContentAsync();
            if (pageText != null
                && pageText.IndexOf("License Agreement for JetBrains", StringComparison.OrdinalIgnoreCase)
                >= 0)
            {
                var acceptCheckbox = page.Locator(
                    "input[type='checkbox'][name='accept'], input[id='accept'], input[name='acceptLicense']");

                if (await acceptCheckbox.CountAsync() > 0)
                {
                    LogHelper.LogInfo("Found license checkbox, checking it...", 1);
                    await acceptCheckbox.First.CheckAsync();
                    await Task.Delay(1000); // Wait for JavaScript to enable the button

                    var continueButton = page.Locator(
                        "input[type='submit'][name='Continue'], input[type='submit'].submitButton, button:has-text('Continue'), input[value*='Continue']");

                    if (await continueButton.CountAsync() == 0)
                    {
                        LogHelper.LogError("Continue button not found on license page");
                        await TakeScreenshot(page, "error_license_no_continue");
                        return false;
                    }

                    // Wait for button to be enabled (JavaScript enables it after checkbox is checked)
                    LogHelper.LogInfo("Waiting for Continue button to be enabled...", 1);
                    try
                    {
                        await continueButton.First.WaitForAsync(
                            new LocatorWaitForOptions
                            {
                                State = WaitForSelectorState.Visible, Timeout = 5000
                            });
                    }
                    catch (Exception ex)
                    {
                        LogHelper.LogWarning($"Could not wait for button visibility: {ex.Message}", 1);
                    }

                    LogHelper.LogInfo("Clicking Continue after license acceptance...", 1);
                    await continueButton.First.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await Task.Delay(3000);
                    await TakeScreenshot(page, "07_after_license");

                    // Ensure license text disappeared
                    await WaitForTextToDisappearAsync(page, "License Agreement for JetBrains", 30);
                    var postLicenseText = await page.ContentAsync();
                    if (postLicenseText != null
                        && postLicenseText.IndexOf(
                            "License Agreement for JetBrains",
                            StringComparison.OrdinalIgnoreCase)
                        >= 0)
                    {
                        LogHelper.LogError("License acceptance did not complete successfully");
                        await TakeScreenshot(page, "error_license_still_present");
                        return false;
                    }

                    LogHelper.LogInfo("License accepted", 1);
                }
                else
                {
                    LogHelper.LogInfo("License checkbox not found, skipping license acceptance step", 1);
                }
            }
            else
            {
                LogHelper.LogInfo("License page not detected, skipping license acceptance step", 1);
            }

            // Step 4: Create Administrator Account
            LogHelper.Log("Step 4: Checking for admin account creation");
            await Task.Delay(2000);
            await TakeScreenshot(page, "08_before_admin_creation");

            var usernameField = page.Locator("input[name='username'], input[id='input_teamcityUsername']");
            if (await usernameField.CountAsync() > 0)
            {
                LogHelper.LogInfo("Found admin creation form, filling in details...", 1);
                await usernameField.First.FillAsync(username);
                await Task.Delay(300);

                var passwordField = page.Locator(
                        "input[name='password'], input[id='password1'], input[type='password']")
                    .First;

                await passwordField.FillAsync(password);
                await Task.Delay(300);

                var confirmPasswordField = page.Locator(
                    "input[name='confirmPassword'], input[id='password2'], input[name='retypedPassword']");

                if (await confirmPasswordField.CountAsync() > 0)
                {
                    await confirmPasswordField.First.FillAsync(password);
                    await Task.Delay(300);
                }

                await TakeScreenshot(page, "09_admin_form_filled");

                var createAccountButton = page.Locator(
                    "button:has-text('Create Account'), input[value='Create Account'], button[type='submit']");

                if (await createAccountButton.CountAsync() > 0)
                {
                    LogHelper.LogInfo("Submitting admin account creation...", 1);
                    await createAccountButton.First.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await Task.Delay(5000); // Give TeamCity time to fully initialize
                    await TakeScreenshot(page, "10_after_admin_creation");
                    LogHelper.LogSuccess("Admin account created successfully", 1);
                }
            }
            else
            {
                LogHelper.LogInfo("Admin account may already exist, continuing...", 1);
            }

            // Navigate to token creation page and attempt to create/access token
            LogHelper.Log("Step 5: Creating access token (UI)");
            try
            {
                await page.GotoAsync(
                    $"{teamcityUrl}/profile.html?item=accessTokens",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });

                await Task.Delay(2000);
                await TakeScreenshot(page, "19_token_page");
            }
            catch (Exception ex)
            {
                LogHelper.LogWarning($"Could not navigate to token page: {ex.Message}", 1);
                await TakeScreenshot(page, "19_token_page_error");
            }

            // Try to find existing token
            var existingToken = page.Locator(
                "td:has-text('bootstrap-automation'), span:has-text('bootstrap-automation'), div:has-text('bootstrap-automation')");

            if (await existingToken.CountAsync() > 0)
            {
                LogHelper.LogSuccess(
                    "TeamCity token 'bootstrap-automation' already exists - skipping creation",
                    1);

                await TakeScreenshot(page, "19_token_already_exists");
                return true;
            }

            // Try to create token using UI
            var createTokenButton = page.Locator(
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
                LogHelper.LogInfo(
                    $"Found create token button (matched {await createTokenButton.CountAsync()} elements)",
                    1);

                await createTokenButton.First.ClickAsync();
                await Task.Delay(1000);
                await TakeScreenshot(page, "20_token_creation_dialog");

                var tokenNameInput = page.Locator(
                    "input[name='tokenName'], input[id='tokenName'], input[name='name'], "
                    + "input[placeholder*='name'], input[placeholder*='Token'], input[aria-label*='name'], textarea[name='name'], "
                    + "input#input_accessTokenName, input[name='prop:accessTokenName']"
                );

                if (await tokenNameInput.CountAsync() == 0)
                {
                    var dialog = page.Locator("[role='dialog'], div.modal, div[aria-modal='true']");
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
                        LogHelper.LogInfo("Filling token name 'bootstrap-automation'", 2);
                        await tokenNameInput.First.FillAsync("bootstrap-automation");
                        await Task.Delay(500);
                        await TakeScreenshot(page, "20b_token_name_filled");

                        var createButton = page.Locator(
                            "button:has-text('Create'), input[value='Create'], button[type='submit'], button:has-text('Generate')");

                        if (await createButton.CountAsync() > 0)
                        {
                            LogHelper.LogInfo("Clicking Create/Generate button", 2);
                            await createButton.First.ClickAsync();
                            await Task.Delay(2000);
                            await TakeScreenshot(page, "21_token_created");
                            // Wait for the created token element to appear (#createdToken) or the accessTokenValue row to be shown
                            try
                            {
                                var createdTokenLocator = page.Locator("#createdToken");
                                var accessTokenRow = page.Locator("#accessTokenValue");
                                if (await accessTokenRow.CountAsync() > 0)
                                {
                                    // Wait for the row to become visible
                                    await accessTokenRow.First.WaitForAsync(
                                        new LocatorWaitForOptions
                                        {
                                            State = WaitForSelectorState.Visible, Timeout = 5000
                                        });
                                }

                                if (await createdTokenLocator.CountAsync() > 0)
                                {
                                    var token = (await createdTokenLocator.First.TextContentAsync())?.Trim();
                                    if (string.IsNullOrWhiteSpace(token))
                                    {
                                        // Sometimes the token may be in a child or as a value attribute
                                        token = (await createdTokenLocator.First.GetAttributeAsync("value"))
                                            ?.Trim();
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
                                        LogHelper.LogSuccess("TeamCity token created and saved to .env", 3);
                                    }
                                    else
                                    {
                                        LogHelper.LogWarning(
                                            "Token created but '#createdToken' was empty",
                                            3);
                                    }
                                }
                                else
                                {
                                    LogHelper.LogWarning(
                                        "Token created but '#createdToken' element not found",
                                        3);
                                }
                            }
                            catch (Exception ex)
                            {
                                LogHelper.LogWarning(
                                    $"Exception while extracting created token: {ex.Message}",
                                    3);
                            }
                        }
                        else
                        {
                            LogHelper.LogWarning("Could not find Create/Generate button in dialog", 2);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHelper.LogWarning($"Exception during token creation: {ex.Message}", 2);
                    }
                }
                else
                {
                    LogHelper.LogWarning(
                        "Could not find token name input field (tried multiple selectors)",
                        2);

                    try
                    {
                        var fallbackSubmit = page.Locator(
                            "button:has-text('Create'), button:has-text('Generate'), input[type='submit']");

                        if (await fallbackSubmit.CountAsync() > 0)
                        {
                            LogHelper.LogInfo("Attempting fallback submit for token creation", 2);
                            await fallbackSubmit.First.ClickAsync();
                            await Task.Delay(1500);
                            await TakeScreenshot(page, "21_token_created_fallback");
                        }
                    }
                    catch { }
                }
            }
            else
            {
                LogHelper.LogWarning("Could not find 'Create access token' button", 1);
                var currentUrl = page.Url;
                LogHelper.LogInfo($"Current URL: {currentUrl}", 1);
            }

            await TakeScreenshot(page, "22_final_state");
            LogHelper.LogSuccess("TeamCity automated setup completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            LogHelper.LogError($"TeamCity automated setup failed: {ex.Message}");
            LogHelper.LogError($"Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    public async Task<string?> TryCreateTokenViaApiAsync(
        HttpClient client,
        string teamcityUrl,
        string username,
        string password,
        string tokenName)
    {
        LogHelper.LogInfo(
            $"Attempting API token creation with username '{username}' and tokenName '{tokenName}'",
            1);

        var endpoints = new[]
        {
            $"users/username:{username}/tokens",
            $"users/{username}/tokens",
            "users/id:1/tokens",
            "users/current/tokens"
        };

        return await RetryHelper.RetryAsync(
            async () =>
            {
                foreach (var endpoint in endpoints)
                {
                    var url = ApiUrlHelper.BuildTeamCityApiUrl(teamcityUrl, endpoint);
                    LogHelper.LogInfo($"Trying endpoint: {url}", 2);

                    // Try XML body first
                    var token = await TryCreateTokenWithBodyAsync(
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
                    LogHelper.LogInfo("Trying with JSON body...", 2);
                    token = await TryCreateTokenWithBodyAsync(
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
            },
            maxAttempts: 3,
            baseDelayMs: 2000);
    }

    private async Task<string?> TryCreateTokenWithBodyAsync(
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

            LogHelper.LogInfo($"Response status: {(int)response.StatusCode} {response.StatusCode}", 3);

            if (!response.IsSuccessStatusCode)
            {
                LogHelper.LogInfo(
                    $"Response body: {respText.Substring(0, Math.Min(200, respText.Length))}",
                    3);

                return null;
            }

            LogHelper.LogSuccess("Success! Parsing response...", 3);
            var token = ResponseParser.TryParseTokenFromResponse(respText);

            if (token != null)
            {
                LogHelper.LogSuccess($"Token extracted (length: {token.Length})", 3);
                return token;
            }

            LogHelper.LogWarning("Success response but couldn't extract token", 3);
            return null;
        }
        catch (Exception ex)
        {
            LogHelper.LogInfo($"Exception: {ex.Message}", 3);
            return null;
        }
    }

    public async Task<bool> ValidateTeamCityTokenAsync(HttpClient client, string teamcityUrl, string token)
    {
        try
        {
            var apiUrl = ApiUrlHelper.BuildTeamCityApiUrl(teamcityUrl, "server");
            var request = HttpRequestHelper.CreateWithBearerAuth(HttpMethod.Get, apiUrl, token);

            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var serverData = await response.Content.ReadAsStringAsync();
                LogHelper.LogInfo("Token authentication successful", 1);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            LogHelper.LogError($"Validation error: {ex.Message}", 1);
            return false;
        }
    }

    public async Task<bool> CreateProjectAsync(HttpClient client, string teamcityUrl, string token)
    {
        var apiUrl = ApiUrlHelper.BuildTeamCityApiUrl(teamcityUrl, "projects");

        LogHelper.Log($"Creating TeamCity project 'Sample Project' via {apiUrl}");

        try
        {
            var xml = """<newProjectDescription name="Sample Project" id="SampleProject" />""";
            var request = HttpRequestHelper.CreateWithBearerAuth(HttpMethod.Post, apiUrl, token);
            request.AddJsonAccept();
            request.SetXmlContent(xml);

            var response = await client.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                LogHelper.LogSuccess("TeamCity project created successfully");
                return true;
            }

            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.Conflict
                || body.Contains("DuplicateProjectNameException")
                || body.Contains("already exists"))
            {
                LogHelper.Log("TeamCity project already exists");
                return true;
            }

            LogHelper.LogError($"TeamCity API error {(int)response.StatusCode}: {body}");
            return false;
        }
        catch (Exception ex)
        {
            LogHelper.LogError($"Failed to call TeamCity API: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> AuthorizeAgentsAsync(HttpClient client, string teamcityUrl, string token)
    {
        var apiUrl = ApiUrlHelper.BuildTeamCityApiUrl(teamcityUrl, "agents");

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
                LogHelper.LogError($"Failed to get agents list: {(int)listResponse.StatusCode}");
                return false;
            }

            var agentsData = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
            if (!agentsData.TryGetProperty("agent", out var agents))
            {
                LogHelper.Log("No unauthorized agents found");
                return true;
            }

            var authorizedCount = 0;
            foreach (var agent in agents.EnumerateArray())
            {
                var agentId = agent.GetProperty("id").GetInt32();
                var agentName = agent.TryGetProperty("name", out var name)
                    ? name.GetString()
                    : $"agent-{agentId}";

                LogHelper.Log($"Authorizing agent: {agentName} (ID: {agentId})");

                var authRequest = HttpRequestHelper.CreateWithBearerAuth(
                    HttpMethod.Put,
                    $"{apiUrl}/id:{agentId}/authorized",
                    token);

                authRequest.Content = new StringContent("true", Encoding.UTF8, "text/plain");

                var authResponse = await client.SendAsync(authRequest);
                if (authResponse.IsSuccessStatusCode)
                {
                    LogHelper.LogSuccess($"Agent {agentName} authorized", 1);
                    authorizedCount++;

                    var poolApiUrl = ApiUrlHelper.BuildTeamCityApiUrl(teamcityUrl, "agentPools/id:0/agents");
                    var poolRequest = HttpRequestHelper.CreateWithBearerAuth(
                        HttpMethod.Post,
                        poolApiUrl,
                        token);

                    poolRequest.SetXmlContent($"<agent id=\"{agentId}\" />");

                    var poolResponse = await client.SendAsync(poolRequest);
                    if (poolResponse.IsSuccessStatusCode)
                    {
                        LogHelper.LogSuccess($"Agent {agentName} added to default pool", 1);
                    }
                    else
                    {
                        LogHelper.LogWarning(
                            $"Could not add agent {agentName} to pool: {(int)poolResponse.StatusCode}",
                            1);
                    }
                }
                else
                {
                    LogHelper.LogWarning(
                        $"Failed to authorize agent {agentName}: {(int)authResponse.StatusCode}",
                        1);
                }
            }

            LogHelper.Log($"Authorized {authorizedCount} agent(s)");
            return authorizedCount > 0;
        }
        catch (Exception ex)
        {
            LogHelper.LogError($"Failed to authorize agents: {ex.Message}");
            return false;
        }
    }
}