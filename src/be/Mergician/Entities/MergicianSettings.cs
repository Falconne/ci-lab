namespace Mergician.Entities;

public class MergicianSettings
{
    public GitLabSettings GitLab { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
}

public class GitLabSettings
{
    public string Url { get; set; } = "";
    public string InternalUrl { get; set; } = "";
    public string ServiceToken { get; set; } = "";
    public OAuthSettings OAuth { get; set; } = new();

    /// <summary>
    /// Returns InternalUrl if configured, otherwise falls back to Url.
    /// Use this for server-side HTTP calls to GitLab (token exchange, API requests).
    /// Use Url for browser-facing redirects (OAuth authorize).
    /// </summary>
    public string ServerUrl => string.IsNullOrWhiteSpace(InternalUrl) ? Url : InternalUrl;
}

public class OAuthSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
