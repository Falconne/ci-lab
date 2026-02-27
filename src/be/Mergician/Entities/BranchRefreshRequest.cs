namespace Mergician.Entities;

// TODO: Get rid of this class. The frontend should not be requesting refreshes with all this detail. Refactor the code
// so that the frontend just sends `DashboardPollRequest` everywhere and the backend can look up what it needs from the
// database. LastUpdated and MergeGroupId need not be sent by the frontend. When responding to a refresh-activity poll,
// the last updated time for a branch should be determined by a Gitlab API call, just like approvals status is done,
// and returned as part of the activity refresh info.

/// <summary>
///     Request to refresh the MR/approval status for a specific branch in a project.
/// </summary>
public record BranchRefreshRequest(
    string BranchName,
    int ProjectId,
    DateTimeOffset? LastUpdated = null,
    int? MergeGroupId = null);