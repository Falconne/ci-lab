namespace IntegrationTest;

public static class TestConfig
{
    // CI Lab test user credentials (matches Bootstrap's ProjectSetupService)
    public const string TestUsername = "test1";

    public const string TestPassword = "changeme123";

    public const string TestEmail = "test1@CILab.local";

    // URLs
    public const string GitLabUrl = "http://localhost:8081";

    public const string MergicianUrl = "http://localhost:5000";

    // Paths
    private static readonly string RepoRoot = FindRepoRoot();

    public static readonly string ScreenshotDir = Path.Combine(RepoRoot, "data", "screenshots", "integration-test");

    public static readonly string LogDir = Path.Combine(RepoRoot, "data", "logs");

    private static readonly string EnvFilePath = Path.Combine(RepoRoot, ".env");

    /// <summary>
    ///     Reads a value from the .env file. Returns null if the key is not found.
    /// </summary>
    public static string? GetEnvValue(string key)
    {
        if (!File.Exists(EnvFilePath))
        {
            return null;
        }

        foreach (var line in File.ReadAllLines(EnvFilePath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('='))
            {
                continue;
            }

            var eqIndex = trimmed.IndexOf('=');
            var lineKey = trimmed[..eqIndex].Trim();
            if (lineKey == key)
            {
                var value = trimmed[(eqIndex + 1)..].Trim();
                // Strip surrounding quotes if present
                if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                {
                    value = value[1..^1];
                }

                return value;
            }
        }

        return null;
    }

    /// <summary>
    ///     Gets the GitLab admin token from the .env file.
    /// </summary>
    public static string GetGitLabAdminToken()
    {
        return GetEnvValue("GITLAB_TOKEN")
               ?? throw new InvalidOperationException("GITLAB_TOKEN not found in .env file");
    }

    /// <summary>
    ///     Gets the GitLab PAT for a test user (e.g. "test1" -> GITLAB_TEST1_TOKEN).
    /// </summary>
    public static string GetTestUserToken(string username)
    {
        var index = username.Replace("test", "");
        var key = $"GITLAB_TEST{index}_TOKEN";
        return GetEnvValue(key)
               ?? throw new InvalidOperationException($"{key} not found in .env file");
    }

    /// <summary>
    ///     Searches upward from <see cref="AppContext.BaseDirectory" /> for a 'data' directory
    ///     containing a '.placeholder' file. Returns the parent directory (repository root).
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            var dataDir = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(dataDir) && File.Exists(Path.Combine(dataDir, ".placeholder")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root. Expected a 'data' directory containing '.placeholder' " +
            $"in an ancestor of '{AppContext.BaseDirectory}'.");
    }
}