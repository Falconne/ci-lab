namespace Mergician.Entities;

// TODO: Get rid of this class. The frontend should not be requesting refreshes with all this detail. Refactor the code
// so that the frontend just sends `DashboardPollRequest` everywhere and the backend can look up what it needs from the
// database. MergeGroupId need not be sent by the frontend and LastUpdated time can be ignored.

/// <summary>
///     Request to refresh the MR/approval status for a specific branch in a project.
/// </summary>
public record BranchRefreshRequest(
    string BranchName,
    int ProjectId,
    DateTimeOffset? LastUpdated = null,
    int? MergeGroupId = null);