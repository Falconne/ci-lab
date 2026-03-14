namespace Mergician.Services.Authentication;

// TODO: Inline the base class `AccessDetailsBase` into this class; there is no reason to separate these. Update
// references in the code. Also, if there are methods that currently take in a `AccessDetailsBase` and `userId`
// separately, change them to just take in an `AccessDetailsForUser` and use the `UserId` property when needed.

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