namespace Mergician.Entities;

// TODO: Delete this class and stop returning this as an SSE event. The refresh events should continue to detect for deleted branches
// for removing them from the database, but the frontend should only remove them when the diff from the refresh-branches polling
// tells it to. After this class is deleted, clean up any code from the backend and frontend that has now become redundant.

/// <summary>
///     Notification sent to the frontend when a tracked branch has been deleted
///     from the repository and needs to be removed from the dashboard.
/// </summary>
public record BranchDeletedNotification(
    string BranchName,
    int ProjectId,
    int? MergeGroupId,
    int? BranchInProjectId = null);