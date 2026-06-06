namespace Mergician.Services.Database;

public interface IIgnoredBranchRepository
{
    Task AddIgnoredBranch(int userId, string branchName);

    Task RemoveIgnoredBranch(int userId, string branchName);

    Task<HashSet<string>> GetIgnoredBranchNames(int userId);
}