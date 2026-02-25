namespace Mergician.Entities;

/// <summary>
///     Response from the dashboard poll endpoint containing the diff between
///     the frontend's currently displayed branches and the database state.
/// </summary>
public record DashboardPollResponse(
    List<BranchActivity> Added,
    List<KnownBranch> Removed);
