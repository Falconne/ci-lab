using Dapper;

namespace Mergician.Services.Database;

public class IgnoredBranchRepository : IIgnoredBranchRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly ILogger<IgnoredBranchRepository> _logger;

    public IgnoredBranchRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<IgnoredBranchRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task AddIgnoredBranch(int userId, string branchName)
    {
        _logger.LogDebug("Marking branch '{BranchName}' as ignored for user {UserId}", branchName, userId);
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        await connection.ExecuteAsync(
            """
            INSERT INTO ignored_branches (user_id, branch_name)
            VALUES (@UserId, @BranchName)
            ON CONFLICT (user_id, branch_name) DO NOTHING
            """,
            new { UserId = userId, BranchName = branchName });
    }

    public async Task RemoveIgnoredBranch(int userId, string branchName)
    {
        _logger.LogDebug(
            "Removing ignored status for branch '{BranchName}' for user {UserId}",
            branchName,
            userId);

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        await connection.ExecuteAsync(
            "DELETE FROM ignored_branches WHERE user_id = @UserId AND branch_name = @BranchName",
            new { UserId = userId, BranchName = branchName });
    }

    public async Task<HashSet<string>> GetIgnoredBranchNames(int userId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        var names = await connection.QueryAsync<string>(
            "SELECT branch_name FROM ignored_branches WHERE user_id = @UserId",
            new { UserId = userId });

        return [.. names];
    }
}