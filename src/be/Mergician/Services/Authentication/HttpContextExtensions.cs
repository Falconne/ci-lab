namespace Mergician.Services.Authentication;

/// <summary>
///     Extension methods for retrieving the authenticated UserAccessDetails
///     from the current HTTP context. Used by controllers after [Authorize]
///     ensures the user is authenticated.
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    ///     Gets the UserAccessDetails stored by the authentication handler.
    ///     Throws if the user is not authenticated (should not happen
    ///     when used with [Authorize]).
    /// </summary>
    public static UserAccessDetails GetGitLabUser(this HttpContext context)
    {
        return context.Items[GitLabCookieAuthenticationHandler.GitLabUserAccessDetailsKey] as UserAccessDetails
               ?? throw new InvalidOperationException(
                   "Authenticated GitLab user was not found in HttpContext items.");
    }
}