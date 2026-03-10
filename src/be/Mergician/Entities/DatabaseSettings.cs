namespace Mergician.Entities;

public class DatabaseSettings
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 5432;

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string Database { get; set; } = "mergician";

    /// <summary>
    ///     Returns a connection string for the configured database.
    /// </summary>
    public string ConnectionString =>
        $"Host={Host};Port={Port};Username={Username};Password={Password};Database={Database}";

    /// <summary>
    ///     Returns a connection string to the default 'postgres' database,
    ///     used for creating the target database if it doesn't exist.
    /// </summary>
    public string AdminConnectionString =>
        $"Host={Host};Port={Port};Username={Username};Password={Password};Database=postgres";
}