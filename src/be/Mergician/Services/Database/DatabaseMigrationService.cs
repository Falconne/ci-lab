using DbUp;
using Mergician.Entities;
using Npgsql;
using Serilog;

namespace Mergician.Services.Database;

/// <summary>
/// Handles database creation and schema migrations on application startup.
/// Uses DbUp to run embedded SQL migration scripts in order.
/// </summary>
public class DatabaseMigrationService
{
    private readonly DatabaseSettings _settings;

    public DatabaseMigrationService(DatabaseSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Ensures the target database exists and runs all pending migrations.
    /// Throws on failure so the application does not start with an inconsistent schema.
    /// </summary>
    public void MigrateDatabase()
    {
        EnsureDatabaseExists();
        RunMigrations();
    }

    private void EnsureDatabaseExists()
    {
        Log.Information("Checking if database '{Database}' exists", _settings.Database);

        using var connection = new NpgsqlConnection(_settings.AdminConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @dbName";
        cmd.Parameters.AddWithValue("dbName", _settings.Database);

        var exists = cmd.ExecuteScalar() != null;

        if (!exists)
        {
            Log.Information("Database '{Database}' does not exist, creating it", _settings.Database);

            using var createCmd = connection.CreateCommand();
            // Database names cannot be parameterized in CREATE DATABASE
            createCmd.CommandText = $"CREATE DATABASE \"{_settings.Database}\"";
            createCmd.ExecuteNonQuery();

            Log.Information("Database '{Database}' created successfully", _settings.Database);
        }
        else
        {
            Log.Information("Database '{Database}' already exists", _settings.Database);
        }
    }

    private void RunMigrations()
    {
        Log.Information("Running database migrations against '{Database}'", _settings.Database);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(_settings.ConnectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrationService).Assembly)
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            Log.Error(result.Error, "Database migration failed");
            throw new InvalidOperationException(
                $"Database migration failed: {result.Error.Message}", result.Error);
        }

        Log.Information("Database migrations completed successfully. Scripts executed: {Count}",
            result.Scripts.Count());
    }
}
