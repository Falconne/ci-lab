namespace Mergician.Services.Authentication;

/// <summary>
///     Extension methods for retrieving the authenticated AccessDetailsForUser
///     from the current HTTP context. Used by controllers after [Authorize]
///     ensures the user is authenticated.
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    ///     Gets the AccessDetailsForUser stored by the authentication handler.
    ///     Throws if the user is not authenticated (should not happen
    ///     when used with [Authorize]).
    /// </summary>
    public static AccessDetailsForUser GetGitlabUser(this HttpContext context)
    {
        return context.Items[GitLabCookieAuthenticationHandler.GitlabAccessUserKey] as AccessDetailsForUser
               ?? throw new InvalidOperationException(
                   "Authenticated GitLab user was not found in HttpContext items.");
    }
}