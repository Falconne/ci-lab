using Mergician.Entities;
using Npgsql;
using System.Data;

namespace Mergician.Services.Database;

/// <summary>
///     Factory for creating database connections. Abstracted via interface for testability.
/// </summary>
public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}

public class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(DatabaseSettings settings)
    {
        _connectionString = settings.ConnectionString;
    }

    public IDbConnection CreateConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }
}