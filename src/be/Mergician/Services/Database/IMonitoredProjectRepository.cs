using Mergician.Entities.Database;

namespace Mergician.Services.Database;

/// <summary>
///     Repository interface for managing the list of monitored GitLab projects.
///     Monitored projects are polled by <see cref="MonitoredProjectsService" /> to automatically
///     control auto merge based on the presence of the "AutoMerge" label on MRs.
/// </summary>
public interface IMonitoredProjectRepository
{
    /// <summary>
    ///     Returns all monitored projects.
    /// </summary>
    List<MonitoredProject> GetAll();

    /// <summary>
    ///     Returns the project IDs of all monitored projects.
    /// </summary>
    List<int> GetAllProjectIds();

    /// <summary>
    ///     Returns true if the given project ID is in the monitored list.
    /// </summary>
    bool IsMonitoredProject(int projectId);

    /// <summary>
    ///     Adds a project to the monitored list. Updates the project name if the project is already monitored.
    ///     Returns the inserted or updated record.
    /// </summary>
    MonitoredProject Upsert(int projectId, string? projectName);

    /// <summary>
    ///     Removes a project from the monitored list.
    ///     Returns true if the project was removed, false if it was not found.
    /// </summary>
    bool Remove(int projectId);
}