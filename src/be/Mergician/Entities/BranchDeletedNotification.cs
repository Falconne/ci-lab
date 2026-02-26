namespace Mergician.Entities;

/// <summary>
/// Notification sent to the frontend when a tracked branch has been deleted
/// from the repository and needs to be removed from the dashboard.
/// </summary>
public record BranchDeletedNotification(
    string BranchName,
    int ProjectId,
    int? MergeGroupId,
    int? BranchInProjectId = null);
