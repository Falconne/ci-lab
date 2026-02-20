namespace Mergician.Services.Database;

/// <summary>
/// Repository interface for user-scoped database operations.
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// Gets the last poll timestamp for a GitLab user, or null if the user has never been polled.
    /// </summary>
    DateTime? GetLastPollTimestamp(int gitlabUserId);

    /// <summary>
    /// Updates the last poll timestamp for a user, creating the record if needed.
    /// </summary>
    void UpsertLastPollTimestamp(int gitlabUserId, DateTime timestamp);
}
