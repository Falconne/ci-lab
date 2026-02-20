using Dapper;
using Serilog;

namespace Mergician.Services.Database;

/// <summary>
/// Dapper-based implementation of user activity timestamp persistence.
/// All timestamps are stored and returned in UTC.
/// </summary>
public class UserRepository : IUserRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public UserRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public DateTime? GetLastPollTimestamp(int gitlabUserId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var result = connection.QueryFirstOrDefault<DateTime?>(
            "SELECT last_poll_timestamp FROM user_activity WHERE gitlab_user_id = @GitlabUserId",
            new { GitlabUserId = gitlabUserId });

        if (result.HasValue)
        {
            Log.Debug("Retrieved last poll timestamp for user {UserId}: {Timestamp}", gitlabUserId, result.Value);
        }
        else
        {
            Log.Debug("No last poll timestamp found for user {UserId}", gitlabUserId);
        }

        return result.HasValue ? DateTime.SpecifyKind(result.Value, DateTimeKind.Utc) : null;
    }

    public void UpsertLastPollTimestamp(int gitlabUserId, DateTime timestamp)
    {
        var utcTimestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        connection.Execute(
            """
            INSERT INTO user_activity (gitlab_user_id, last_poll_timestamp)
            VALUES (@GitlabUserId, @Timestamp)
            ON CONFLICT (gitlab_user_id)
            DO UPDATE SET last_poll_timestamp = @Timestamp
            """,
            new { GitlabUserId = gitlabUserId, Timestamp = utcTimestamp });

        Log.Debug("Upserted last poll timestamp for user {UserId}: {Timestamp}", gitlabUserId, utcTimestamp);
    }
}
