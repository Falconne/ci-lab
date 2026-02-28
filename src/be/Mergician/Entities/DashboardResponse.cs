namespace Mergician.Entities;

/// <summary>
///     Response from the dashboard poll endpoint containing the full current state
///     of all branches for the authenticated user.
///     The frontend reconciles this against its current display state.
/// </summary>
public record DashboardResponse(List<BranchRecord> Branches);
