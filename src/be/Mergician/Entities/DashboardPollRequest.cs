namespace Mergician.Entities;

/// <summary>
///     Request payload for the dashboard poll endpoint.
///     The frontend sends the branches it currently displays so the backend
///     can compute a minimal diff against the current database state.
/// </summary>
public record DashboardPollRequest(List<KnownBranch> KnownBranches);

/// <summary>
///     Identifies a branch the frontend currently displays.
/// </summary>
public record KnownBranch(string BranchName, int ProjectId);
