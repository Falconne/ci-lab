namespace Mergician.Entities;

/// <summary>
///     Internal result of a view permission check in <see cref="Mergician.Services.MergePermissionService" />.
///     Not sent to the frontend directly; used to decide whether to return the MG or a 403 response.
/// </summary>
public record ViewPermissionsResult(bool CanView, bool CheckFailed, List<string> DeniedProjects);