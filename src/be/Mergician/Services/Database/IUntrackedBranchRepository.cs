namespace Mergician.Services.Database;

public interface IUntrackedBranchRepository
{
    Task AddUntrackedBranch(int userId, string branchName);

    Task RemoveUntrackedBranch(int userId, string branchName);

    Task<HashSet<string>> GetUntrackedBranchNames(int userId);
}
