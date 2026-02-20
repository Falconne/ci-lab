using Dapper;

namespace Mergician.Services.Database;

/// <summary>
/// Dapper-based implementation of global database operations.
/// </summary>
public class CoreRepository : ICoreRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly ILogger<CoreRepository> _logger;

    public CoreRepository(IDbConnectionFactory connectionFactory, ILogger<CoreRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public bool IsHealthy()
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            connection.Execute("SELECT 1");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database health check failed");
            return false;
        }
    }
}
