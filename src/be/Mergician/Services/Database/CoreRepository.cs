using Dapper;
using Serilog;

namespace Mergician.Services.Database;

/// <summary>
/// Dapper-based implementation of global database operations.
/// </summary>
public class CoreRepository : ICoreRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public CoreRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
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
            Log.Warning(ex, "Database health check failed");
            return false;
        }
    }
}
