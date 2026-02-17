namespace Mergician.Services.Authentication;

/// <summary>
///     Extension methods for retrieving the authenticated GitlabAccessUser
///     from the current HTTP context. Used by controllers after [Authorize]
///     ensures the user is authenticated.
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    ///     Gets the GitlabAccessUser stored by the authentication handler.
    ///     Throws if the user is not authenticated (should not happen
    ///     when used with [Authorize]).
    /// </summary>
    public static GitlabAccessUser GetGitlabUser(this HttpContext context)
    {
        var gitlabUser = context.Items[GitLabCookieAuthenticationHandler.GitlabAccessUserKey] as GitlabAccessUser;
        if (gitlabUser is null)
            throw new InvalidOperationException("Authenticated GitLab user was not found in HttpContext items.");

        return gitlabUser;
    }
}
