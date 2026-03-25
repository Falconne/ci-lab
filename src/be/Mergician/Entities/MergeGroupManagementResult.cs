namespace Mergician.Entities;

public enum MergeGroupManagementError
{
    InvalidUrl,

    MergeGroupNotFound,

    MergeRequestNotFound
}

// TODO: Replace this with an inlined tuple (i.e. just define the tuple at the return site of the method using this).
public record AddBranchByMrResult(MergeGroup? UpdatedMergeGroup, MergeGroupManagementError? Error);

// TODO: Replace this with an inlined tuple (i.e. just define the tuple at the return site of the method using this).
public record FindMergeGroupByMrResult(int? MergeGroupId, bool WasCreated, MergeGroupManagementError? Error);