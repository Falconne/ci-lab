namespace Mergician.Services.Authentication;

/// <summary>
///     Represents an authenticated GitLab API user with a valid access token and a known GitLab user ID.
///     Instances are created by the GitLabCookieAuthenticationHandler after successful authentication.
/// </summary>
public class AccessDetailsForUser : AccessDetailsBase
{
    public AccessDetailsForUser(string accessToken, string apiBaseUrl, int userId)
        : base(accessToken, apiBaseUrl)
    {
        UserId = userId;
    }

    public int UserId { get; }
}