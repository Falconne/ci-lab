using DbUp;
using Mergician.Entities;
using Npgsql;

namespace Mergician.Services.Database;

/// <summary>
///     Handles database creation and schema migrations on application startup.
///     Uses DbUp to run embedded SQL migration scripts in order.
/// </summary>
public class DatabaseMigrationService
{
    private readonly ILogger<DatabaseMigrationService> _logger;

    private readonly DatabaseSettings _settings;

    public DatabaseMigrationService(DatabaseSettings settings, ILogger<DatabaseMigrationService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    ///     Ensures the target database exists and runs all pending migrations.
    ///     Throws on failure so the application does not start with an inconsistent schema.
    /// </summary>
    public void MigrateDatabase()
    {
        EnsureDatabaseExists();
        RunMigrations();
    }

    private void EnsureDatabaseExists()
    {
        _logger.LogInformation("Checking if database '{Database}' exists", _settings.Database);

        using var connection = new NpgsqlConnection(_settings.AdminConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @dbName";
        cmd.Parameters.AddWithValue("dbName", _settings.Database);

        var exists = cmd.ExecuteScalar() != null;

        if (!exists)
        {
            _logger.LogInformation("Database '{Database}' does not exist, creating it", _settings.Database);

            using var createCmd = connection.CreateCommand();
            // Database names cannot be parameterized in CREATE DATABASE
            createCmd.CommandText = $"CREATE DATABASE \"{_settings.Database}\"";
            createCmd.ExecuteNonQuery();

            _logger.LogInformation("Database '{Database}' created successfully", _settings.Database);
        }
        else
        {
            _logger.LogInformation("Database '{Database}' already exists", _settings.Database);
        }
    }

    private void RunMigrations()
    {
        _logger.LogInformation("Running database migrations against '{Database}'", _settings.Database);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(_settings.ConnectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrationService).Assembly)
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            _logger.LogError(result.Error, "Database migration failed");
            throw new InvalidOperationException(
                $"Database migration failed: {result.Error.Message}",
                result.Error);
        }

        _logger.LogInformation(
            "Database migrations completed successfully. Scripts executed: {Count}",
            result.Scripts.Count());
    }
}