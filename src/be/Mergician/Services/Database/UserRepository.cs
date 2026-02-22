using Dapper;
using Mergician.Services.Time;

namespace Mergician.Services.Database;

/// <summary>
/// Dapper-based implementation of user activity timestamp persistence.
/// All timestamps are stored and returned in UTC.
/// </summary>
public class UserRepository : IUserRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly ILogger<UserRepository> _logger;

    public UserRepository(IDbConnectionFactory connectionFactory, ILogger<UserRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public DateTimeOffset? GetLastPollTimestamp(int gitlabUserId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var result = connection.QueryFirstOrDefault<DateTimeOffset?>(
            "SELECT last_poll_timestamp FROM user_activity WHERE gitlab_user_id = @GitlabUserId",
            new { GitlabUserId = gitlabUserId });

        if (result.HasValue)
        {
            _logger.LogDebug("Retrieved last poll timestamp for user {UserId}: {Timestamp}", gitlabUserId, result.Value);
        }
        else
        {
            _logger.LogDebug("No last poll timestamp found for user {UserId}", gitlabUserId);
        }

        return result.HasValue
            ? UtcTimestamp.EnsureUtc(
                result.Value,
                $"UserRepository.GetLastPollTimestamp user {gitlabUserId}",
                _logger)
            : null;
    }

    public void UpsertLastPollTimestamp(int gitlabUserId, DateTimeOffset timestamp)
    {
        var utcTimestamp = UtcTimestamp.EnsureUtc(
            timestamp,
            $"UserRepository.UpsertLastPollTimestamp user {gitlabUserId}",
            _logger);

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

        _logger.LogDebug("Upserted last poll timestamp for user {UserId}: {Timestamp}", gitlabUserId, utcTimestamp);
    }
}
