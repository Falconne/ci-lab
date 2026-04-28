using Mergician.Entities;

namespace Mergician.Services.AutoMerge;

/// <summary>
///     Result of a single merge attempt via the GitLab API.
///     Distinguishes between success, permission failures, and other unexpected failures.
/// </summary>
public record MergeAttemptResult(
    bool Success,
    GitLabMergeResponse? Response = null,
    bool IsPermissionDenied = false)
{
    public static MergeAttemptResult Succeeded(GitLabMergeResponse response) => new(true, response);

    public static MergeAttemptResult Failed(bool isPermissionDenied = false) => new(false, IsPermissionDenied: isPermissionDenied);
}
