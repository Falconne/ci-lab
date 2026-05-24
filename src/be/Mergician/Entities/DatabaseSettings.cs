using System.Text;
using Util;

namespace Mergician.Entities;

public class DatabaseSettings
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 5432;

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string Database { get; set; } = "mergician";

    /// <summary>
    ///     Npgsql SSL mode (e.g. Disable, Allow, Prefer, Require, VerifyCA, VerifyFull).
    ///     Leave empty to use the Npgsql default (Prefer).
    ///     Set to "Require" or "VerifyFull" for managed cloud databases (RDS, Cloud SQL, Azure, etc.).
    /// </summary>
    public string SslMode { get; set; } = "";

    /// <summary>
    ///     Returns a connection string for the configured database.
    /// </summary>
    public string ConnectionString => BuildConnectionString(Database);

    /// <summary>
    ///     Returns a connection string to the default 'postgres' database,
    ///     used for creating the target database if it doesn't exist.
    /// </summary>
    public string AdminConnectionString => BuildConnectionString("postgres");

    private string BuildConnectionString(string database)
    {
        var sb = new StringBuilder(
            $"Host={Host};Port={Port};Username={Username};Password={Password};Database={database};Include Error Detail=true");

        if (SslMode.IsNotEmpty())
            sb.Append($";SslMode={SslMode}");

        return sb.ToString();
    }
}