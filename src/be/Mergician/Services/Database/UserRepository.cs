using Dapper;
using Mergician.Services.Time;

namespace Mergician.Services.Database;

/// <summary>
///     Dapper-based implementation of user activity timestamp persistence.
///     All timestamps are stored and returned in UTC.
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

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT last_poll_timestamp FROM user_activity WHERE gitlab_user_id = @GitlabUserId";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "GitlabUserId";
        parameter.Value = gitlabUserId;
        command.Parameters.Add(parameter);

        var rawResult = command.ExecuteScalar();

        var result = rawResult switch
        {
            null => null,

            DBNull => (DateTimeOffset?)null,

            DateTimeOffset timestampOffset => UtcTimestamp.EnsureUtc(
                timestampOffset,
                () => $"UserRepository.GetLastPollTimestamp user {gitlabUserId} (DateTimeOffset)",
                _logger),

            DateTime timestamp => new DateTimeOffset(
                UtcTimestamp.EnsureUtc(
                    timestamp,
                    () => $"UserRepository.GetLastPollTimestamp user {gitlabUserId} (DateTime)",
                    _logger),
                TimeSpan.Zero),

            _ => throw new InvalidOperationException(
                $"UserRepository.GetLastPollTimestamp expected DateTime or DateTimeOffset but got {rawResult.GetType().FullName}.")
        };

        if (result.HasValue)
        {
            _logger.LogDebug(
                "Retrieved last poll timestamp for user {UserId}: {Timestamp}",
                gitlabUserId,
                result.Value);
        }
        else
        {
            _logger.LogDebug("No last poll timestamp found for user {UserId}", gitlabUserId);
        }

        return result;
    }

    public void UpsertLastPollTimestamp(int gitlabUserId, DateTimeOffset timestamp)
    {
        var utcTimestamp = UtcTimestamp.EnsureUtc(
            timestamp,
            () => $"UserRepository.UpsertLastPollTimestamp user {gitlabUserId}",
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

        _logger.LogDebug(
            "Upserted last poll timestamp for user {UserId}: {Timestamp}",
            gitlabUserId,
            utcTimestamp);
    }
}