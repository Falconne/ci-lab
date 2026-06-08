using Dapper;
using Mergician.Entities;

namespace Mergician.Services.Database;

/// <summary>
///     Dapper-based implementation of <see cref="IMonitoredProjectRepository" />.
/// </summary>
public class MonitoredProjectRepository : IMonitoredProjectRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly ILogger<MonitoredProjectRepository> _logger;

    public MonitoredProjectRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<MonitoredProjectRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public List<MonitoredProject> GetAll()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return connection.Query<MonitoredProject>(
                "SELECT id AS Id, project_id AS ProjectId, project_name AS ProjectName FROM monitored_project ORDER BY id")
            .ToList();
    }

    public List<int> GetAllProjectIds()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return connection.Query<int>("SELECT project_id FROM monitored_project").ToList();
    }

    public bool IsMonitoredProject(int projectId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var exists = connection.QueryFirstOrDefault<int?>(
            "SELECT 1 FROM monitored_project WHERE project_id = @ProjectId",
            new { ProjectId = projectId });

        return exists != null;
    }

    public MonitoredProject Upsert(int projectId, string? projectName)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var record = connection.QueryFirst<MonitoredProject>(
            """
            INSERT INTO monitored_project (project_id, project_name)
            VALUES (@ProjectId, @ProjectName)
            ON CONFLICT (project_id) DO UPDATE SET project_name = EXCLUDED.project_name
            RETURNING id AS Id, project_id AS ProjectId, project_name AS ProjectName
            """,
            new { ProjectId = projectId, ProjectName = projectName });

        _logger.LogInformation(
            "Upserted monitored project: projectId={ProjectId}, name='{ProjectName}'",
            projectId,
            projectName);

        return record;
    }

    public bool Remove(int projectId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var rows = connection.Execute(
            "DELETE FROM monitored_project WHERE project_id = @ProjectId",
            new { ProjectId = projectId });

        if (rows > 0)
        {
            _logger.LogInformation("Removed monitored project with projectId={ProjectId}", projectId);
        }
        else
        {
            _logger.LogDebug(
                "RemoveMonitoredProject: projectId={ProjectId} was not in the monitored list",
                projectId);
        }

        return rows > 0;
    }
}