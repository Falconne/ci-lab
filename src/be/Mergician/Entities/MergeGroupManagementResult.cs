namespace Mergician.Entities;

public enum MergeGroupManagementError
{
    InvalidUrl,

    MergeGroupNotFound,

    MergeRequestNotFound
}

public record AddBranchResult(MergeGroup? UpdatedMergeGroup, MergeGroupManagementError? Error);

public record FindOrCreateMergeGroupResult(int? MergeGroupId, bool WasCreated, MergeGroupManagementError? Error);