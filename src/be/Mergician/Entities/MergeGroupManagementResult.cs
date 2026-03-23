namespace Mergician.Entities;

public enum MergeGroupManagementError
{
    InvalidUrl,
    MergeGroupNotFound,
    MergeRequestNotFound
}

public record AddBranchByMrResult(MergeGroup? UpdatedMergeGroup, MergeGroupManagementError? Error)
{
    public bool IsSuccess => UpdatedMergeGroup != null;
}

public record FindMergeGroupByMrResult(int? MergeGroupId, bool WasCreated, MergeGroupManagementError? Error)
{
    public bool IsSuccess => MergeGroupId.HasValue;
}
