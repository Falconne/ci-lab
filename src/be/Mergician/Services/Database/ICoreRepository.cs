namespace Mergician.Services.Database;

/// <summary>
/// Repository interface for global database operations.
/// </summary>
public interface ICoreRepository
{
    /// <summary>
    /// Checks if the database is reachable by executing a simple query.
    /// </summary>
    bool IsHealthy();
}
