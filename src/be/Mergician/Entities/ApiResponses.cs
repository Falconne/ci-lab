namespace Mergician.Entities;

public record ErrorResponse(string Error);

public record VersionResponse(string Version);

public record SubscriptionResponse(bool IsSubscribed);

public record MergeRequestUrlRequest(string MergeRequestUrl);

public record FindByMergeRequestResponse(int MergeGroupId, bool Created);

public record PermissionsCheckResult(bool HasPermission, bool CheckFailed, List<string> DeniedProjects);

public record AccessDeniedResponse(string Error, List<string> DeniedProjects);