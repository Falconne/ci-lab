using Dapper;

namespace Mergician.Services.Database;

// TODO Rename this class from `UntrackedBranchRepository` to `IgnoredBranchRepository`. Update all
// associated code, including interfaces, variables and database table names to use this new
// naming convention.

public class UntrackedBranchRepository : IUntrackedBranchRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly ILogger<UntrackedBranchRepository> _logger;

    public UntrackedBranchRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<UntrackedBranchRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task AddUntrackedBranch(int userId, string branchName)
    {
        _logger.LogDebug("Marking branch '{BranchName}' as untracked for user {UserId}", branchName, userId);
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        await connection.ExecuteAsync(
            """
            INSERT INTO untracked_branches (user_id, branch_name)
            VALUES (@UserId, @BranchName)
            ON CONFLICT (user_id, branch_name) DO NOTHING
            """,
            new { UserId = userId, BranchName = branchName });
    }

    public async Task RemoveUntrackedBranch(int userId, string branchName)
    {
        _logger.LogDebug(
            "Removing untracked status for branch '{BranchName}' for user {UserId}",
            branchName,
            userId);

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        await connection.ExecuteAsync(
            "DELETE FROM untracked_branches WHERE user_id = @UserId AND branch_name = @BranchName",
            new { UserId = userId, BranchName = branchName });
    }

    public async Task<HashSet<string>> GetUntrackedBranchNames(int userId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        var names = await connection.QueryAsync<string>(
            "SELECT branch_name FROM untracked_branches WHERE user_id = @UserId",
            new { UserId = userId });

        return [.. names];
    }
}