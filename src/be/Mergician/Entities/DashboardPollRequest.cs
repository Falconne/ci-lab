namespace Mergician.Entities;

/// <summary>
///     Request payload for the dashboard poll endpoint.
///     The frontend sends the branch database IDs it currently displays so the backend
///     can compute a minimal diff against the current database state.
/// </summary>
public record DashboardPollRequest(List<KnownBranch> KnownBranches);

/// <summary>
///     Identifies a branch the frontend currently displays, using the database primary key.
/// </summary>
public record KnownBranch(int BranchInProjectId);
