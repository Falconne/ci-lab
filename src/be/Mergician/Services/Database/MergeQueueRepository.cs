using System.Data;
using Dapper;
using Mergician.Entities;

namespace Mergician.Services.Database;

/// <summary>
///     Dapper-based implementation of merge-queue operations.
///     See <see cref="IMergeQueueRepository" /> for the contract and merge/split rules.
/// </summary>
public class MergeQueueRepository : IMergeQueueRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly ILogger<MergeQueueRepository> _logger;

    public MergeQueueRepository(IDbConnectionFactory connectionFactory, ILogger<MergeQueueRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public MergeQueueEntryInfo? GetQueueEntry(int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return connection.QueryFirstOrDefault<MergeQueueEntryInfo>(
            """
            SELECT queue_id AS QueueId, merge_group_id AS MergeGroupId, position AS Position
            FROM merge_queue_entry
            WHERE merge_group_id = @MergeGroupId
            """,
            new { MergeGroupId = mergeGroupId });
    }

    public IReadOnlyList<MergeQueueInfo> GetAllQueues()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return LoadAllQueues(connection);
    }

    public void AddMergeGroupToQueue(int mergeGroupId, IReadOnlyCollection<int> projectIds)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        // No-op if the group is already in a queue.
        var existing = connection.QueryFirstOrDefault<int?>(
            "SELECT queue_id FROM merge_queue_entry WHERE merge_group_id = @MergeGroupId",
            new { MergeGroupId = mergeGroupId },
            transaction);

        if (existing != null)
        {
            _logger.LogDebug(
                "MergeQueueRepository: merge group {MergeGroupId} is already in queue {QueueId}, skipping add",
                mergeGroupId,
                existing.Value);

            return;
        }

        // Find all queues whose project sets intersect with the new group's projects.
        var intersectingQueueIds = FindIntersectingQueueIds(connection, transaction, projectIds);

        int targetQueueId;

        switch (intersectingQueueIds.Count)
        {
            case 0:
                targetQueueId = CreateQueue(connection, transaction, projectIds);
                _logger.LogInformation(
                    "MergeQueueRepository: created new queue {QueueId} for merge group {MergeGroupId} (projects: [{Projects}])",
                    targetQueueId,
                    mergeGroupId,
                    string.Join(", ", projectIds));
                break;

            case 1:
                targetQueueId = intersectingQueueIds[0];
                AddMissingProjectsToQueue(connection, transaction, targetQueueId, projectIds);
                _logger.LogInformation(
                    "MergeQueueRepository: appending merge group {MergeGroupId} to existing queue {QueueId}",
                    mergeGroupId,
                    targetQueueId);
                break;

            default:
                // Multiple queues share projects with the new group — merge them all into one.
                targetQueueId = MergeQueues(connection, transaction, intersectingQueueIds, projectIds);
                _logger.LogInformation(
                    "MergeQueueRepository: merged {Count} queues into queue {QueueId} for merge group {MergeGroupId}",
                    intersectingQueueIds.Count,
                    targetQueueId,
                    mergeGroupId);
                break;
        }

        AppendGroupToQueue(connection, transaction, targetQueueId, mergeGroupId);
        transaction.Commit();
    }

    public void RemoveMergeGroupFromQueue(int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var entry = connection.QueryFirstOrDefault<(int QueueId, int Position)>(
            "SELECT queue_id AS QueueId, position AS Position FROM merge_queue_entry WHERE merge_group_id = @MergeGroupId",
            new { MergeGroupId = mergeGroupId },
            transaction);

        if (entry == default)
        {
            _logger.LogDebug(
                "MergeQueueRepository: merge group {MergeGroupId} is not in any queue, nothing to remove",
                mergeGroupId);

            return;
        }

        var queueId = entry.QueueId;

        connection.Execute(
            "DELETE FROM merge_queue_entry WHERE merge_group_id = @MergeGroupId",
            new { MergeGroupId = mergeGroupId },
            transaction);

        ResequencePositions(connection, transaction, queueId);

        _logger.LogInformation(
            "MergeQueueRepository: removed merge group {MergeGroupId} from queue {QueueId}",
            mergeGroupId,
            queueId);

        transaction.Commit();

        // Check for possible split outside the remove transaction to keep it focused.
        CheckAndSplitQueue(queueId);
    }

    public void CheckAndSplitQueue(int queueId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var entries = LoadQueueEntries(connection, queueId);

        if (entries.Count <= 1)
        {
            if (entries.Count == 0)
                DeleteEmptyQueue(connection, queueId);

            return;
        }

        // Build a project-ID map per merge group entry.
        var projectsPerGroup = LoadProjectIdsForQueueEntries(connection, entries.Select(e => e.MergeGroupId).ToList());

        var components = FindConnectedComponents(entries, projectsPerGroup);

        if (components.Count <= 1)
        {
            _logger.LogDebug(
                "MergeQueueRepository: queue {QueueId} does not need splitting ({Count} entries, 1 component)",
                queueId,
                entries.Count);

            return;
        }

        _logger.LogInformation(
            "MergeQueueRepository: splitting queue {QueueId} into {ComponentCount} queues",
            queueId,
            components.Count);

        using var transaction = connection.BeginTransaction();

        // Collect all project IDs for each component and build new queues.
        foreach (var component in components)
        {
            var allProjects = component
                .SelectMany(e => projectsPerGroup.GetValueOrDefault(e.MergeGroupId, []))
                .ToHashSet();

            var newQueueId = CreateQueue(connection, transaction, allProjects);

            for (var i = 0; i < component.Count; i++)
            {
                connection.Execute(
                    """
                    INSERT INTO merge_queue_entry (queue_id, merge_group_id, position)
                    VALUES (@QueueId, @MergeGroupId, @Position)
                    """,
                    new { QueueId = newQueueId, component[i].MergeGroupId, Position = i + 1 },
                    transaction);
            }

            _logger.LogInformation(
                "MergeQueueRepository: created split queue {NewQueueId} with {Count} entries",
                newQueueId,
                component.Count);
        }

        // Remove original queue (cascade deletes its entries and projects).
        connection.Execute(
            "DELETE FROM merge_queue WHERE id = @QueueId",
            new { QueueId = queueId },
            transaction);

        transaction.Commit();
    }

    public void ReorderQueue(int queueId, IReadOnlyList<int> orderedMergeGroupIds)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var currentEntries = LoadQueueEntries(connection, queueId);
        if (currentEntries.Count == 0)
        {
            _logger.LogWarning(
                "MergeQueueRepository: attempted to reorder non-existent or empty queue {QueueId}",
                queueId);

            return;
        }

        var currentIds = currentEntries.Select(e => e.MergeGroupId).ToHashSet();
        var reordered = new List<int>();

        // Requested order first (only for groups that are actually in the queue).
        foreach (var id in orderedMergeGroupIds)
        {
            if (currentIds.Contains(id))
                reordered.Add(id);
        }

        // Any groups not in the requested list keep their relative order at the end.
        var requestedSet = orderedMergeGroupIds.ToHashSet();
        foreach (var entry in currentEntries)
        {
            if (!requestedSet.Contains(entry.MergeGroupId))
                reordered.Add(entry.MergeGroupId);
        }

        using var transaction = connection.BeginTransaction();

        for (var i = 0; i < reordered.Count; i++)
        {
            connection.Execute(
                """
                UPDATE merge_queue_entry
                SET position = @Position
                WHERE queue_id = @QueueId AND merge_group_id = @MergeGroupId
                """,
                new { QueueId = queueId, MergeGroupId = reordered[i], Position = i + 1 },
                transaction);
        }

        transaction.Commit();

        _logger.LogInformation(
            "MergeQueueRepository: reordered queue {QueueId}: [{Order}]",
            queueId,
            string.Join(", ", reordered));
    }

    public IReadOnlyList<MergeQueueSummary> GetAllQueueSummaries()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        // Get entry counts per queue.
        var entryCounts = connection.Query<(int QueueId, int Count)>(
                "SELECT queue_id AS QueueId, COUNT(*) AS Count FROM merge_queue_entry GROUP BY queue_id")
            .ToDictionary(r => r.QueueId, r => r.Count);

        // Get distinct project names per queue (join through branch_in_project for real names).
        var projectNameRows = connection.Query<(int QueueId, string ProjectName)>(
            """
            SELECT DISTINCT mqp.queue_id AS QueueId, bp.project_name AS ProjectName
            FROM merge_queue_project mqp
            INNER JOIN branch_in_project bp ON bp.project_id = mqp.project_id
            ORDER BY mqp.queue_id, bp.project_name
            """);

        var projectNamesByQueue = projectNameRows
            .GroupBy(r => r.QueueId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ProjectName).Distinct().OrderBy(n => n).ToList());

        var queueIds = connection.Query<int>("SELECT id FROM merge_queue ORDER BY id").ToList();

        return queueIds
            .Select(id =>
            {
                var projects = projectNamesByQueue.GetValueOrDefault(id, []);
                var displayName = projects.Count > 0
                    ? string.Join(", ", projects)
                    : $"Queue {id}";
                var entryCount = entryCounts.GetValueOrDefault(id, 0);
                return new MergeQueueSummary(id, displayName, entryCount);
            })
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private List<int> FindIntersectingQueueIds(
        IDbConnection connection,
        IDbTransaction transaction,
        IReadOnlyCollection<int> projectIds)
    {
        if (projectIds.Count == 0)
            return [];

        return connection.Query<int>(
                """
                SELECT DISTINCT queue_id
                FROM merge_queue_project
                WHERE project_id = ANY(@ProjectIds)
                """,
                new { ProjectIds = projectIds.ToArray() },
                transaction)
            .ToList();
    }

    private int CreateQueue(
        IDbConnection connection,
        IDbTransaction transaction,
        IEnumerable<int> projectIds)
    {
        var queueId = connection.ExecuteScalar<int>(
            "INSERT INTO merge_queue DEFAULT VALUES RETURNING id",
            transaction: transaction);

        var projectArray = projectIds.Distinct().ToArray();
        if (projectArray.Length > 0)
        {
            connection.Execute(
                "INSERT INTO merge_queue_project (queue_id, project_id) VALUES (@QueueId, @ProjectId)",
                projectArray.Select(p => new { QueueId = queueId, ProjectId = p }),
                transaction);
        }

        return queueId;
    }

    private void AddMissingProjectsToQueue(
        IDbConnection connection,
        IDbTransaction transaction,
        int queueId,
        IEnumerable<int> projectIds)
    {
        connection.Execute(
            """
            INSERT INTO merge_queue_project (queue_id, project_id)
            VALUES (@QueueId, @ProjectId)
            ON CONFLICT (queue_id, project_id) DO NOTHING
            """,
            projectIds.Select(p => new { QueueId = queueId, ProjectId = p }),
            transaction);
    }

    /// <summary>
    ///     Merges multiple queues into one via round-robin interleaving.
    ///     Deletes the source queues and returns the ID of the new combined queue.
    /// </summary>
    private int MergeQueues(
        IDbConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<int> queueIds,
        IReadOnlyCollection<int> newProjectIds)
    {
        // Collect entries per queue (in position order) and all project IDs.
        var entriesPerQueue = new List<List<int>>();
        var allProjects = new HashSet<int>(newProjectIds);

        foreach (var queueId in queueIds)
        {
            var entries = connection.Query<(int MergeGroupId, int Position)>(
                    "SELECT merge_group_id AS MergeGroupId, position AS Position FROM merge_queue_entry WHERE queue_id = @QueueId ORDER BY position",
                    new { QueueId = queueId },
                    transaction)
                .Select(e => e.MergeGroupId)
                .ToList();

            entriesPerQueue.Add(entries);

            var queueProjects = connection.Query<int>(
                    "SELECT project_id FROM merge_queue_project WHERE queue_id = @QueueId",
                    new { QueueId = queueId },
                    transaction)
                .ToList();

            foreach (var p in queueProjects)
                allProjects.Add(p);
        }

        // Round-robin interleave: pick one entry from each queue in order.
        var interleaved = Interleave(entriesPerQueue);

        // Create the new combined queue with all project IDs.
        var newQueueId = CreateQueue(connection, transaction, allProjects);

        for (var i = 0; i < interleaved.Count; i++)
        {
            connection.Execute(
                "INSERT INTO merge_queue_entry (queue_id, merge_group_id, position) VALUES (@QueueId, @MergeGroupId, @Position)",
                new { QueueId = newQueueId, MergeGroupId = interleaved[i], Position = i + 1 },
                transaction);
        }

        // Delete old queues (cascade removes their entries and project rows).
        connection.Execute(
            "DELETE FROM merge_queue WHERE id = ANY(@QueueIds)",
            new { QueueIds = queueIds.ToArray() },
            transaction);

        return newQueueId;
    }

    private void AppendGroupToQueue(
        IDbConnection connection,
        IDbTransaction transaction,
        int queueId,
        int mergeGroupId)
    {
        var maxPosition = connection.ExecuteScalar<int>(
            "SELECT COALESCE(MAX(position), 0) FROM merge_queue_entry WHERE queue_id = @QueueId",
            new { QueueId = queueId },
            transaction);

        connection.Execute(
            "INSERT INTO merge_queue_entry (queue_id, merge_group_id, position) VALUES (@QueueId, @MergeGroupId, @Position)",
            new { QueueId = queueId, MergeGroupId = mergeGroupId, Position = maxPosition + 1 },
            transaction);
    }

    private static void ResequencePositions(IDbConnection connection, IDbTransaction transaction, int queueId)
    {
        // Re-number positions 1, 2, 3 ... in existing order to close any gaps.
        connection.Execute(
            """
            UPDATE merge_queue_entry mqe
            SET position = sub.new_position
            FROM (
                SELECT id, ROW_NUMBER() OVER (ORDER BY position) AS new_position
                FROM merge_queue_entry
                WHERE queue_id = @QueueId
            ) sub
            WHERE mqe.id = sub.id AND mqe.queue_id = @QueueId
            """,
            new { QueueId = queueId },
            transaction);
    }

    private List<MergeQueueEntryInfo> LoadQueueEntries(IDbConnection connection, int queueId)
    {
        return connection.Query<MergeQueueEntryInfo>(
                """
                SELECT @QueueId AS QueueId, merge_group_id AS MergeGroupId, position AS Position
                FROM merge_queue_entry
                WHERE queue_id = @QueueId
                ORDER BY position
                """,
                new { QueueId = queueId })
            .ToList();
    }

    private IReadOnlyList<MergeQueueInfo> LoadAllQueues(IDbConnection connection)
    {
        var projectRows = connection.Query<(int QueueId, int ProjectId)>(
                "SELECT queue_id AS QueueId, project_id AS ProjectId FROM merge_queue_project")
            .GroupBy(r => r.QueueId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(r => r.ProjectId).ToList());

        var entryRows = connection.Query<MergeQueueEntryInfo>(
                "SELECT queue_id AS QueueId, merge_group_id AS MergeGroupId, position AS Position FROM merge_queue_entry ORDER BY queue_id, position")
            .GroupBy(r => r.QueueId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<MergeQueueEntryInfo>)g.ToList());

        var queueIds = connection.Query<int>("SELECT id FROM merge_queue ORDER BY id").ToList();

        return queueIds
            .Select(id => new MergeQueueInfo(
                id,
                projectRows.GetValueOrDefault(id, []),
                entryRows.GetValueOrDefault(id, [])))
            .ToList();
    }

    private static Dictionary<int, HashSet<int>> LoadProjectIdsForQueueEntries(
        IDbConnection connection,
        IReadOnlyList<int> mergeGroupIds)
    {
        if (mergeGroupIds.Count == 0)
            return new Dictionary<int, HashSet<int>>();

        var rows = connection.Query<(int MergeGroupId, int ProjectId)>(
            """
            SELECT bmg.merge_group_id AS MergeGroupId, bp.project_id AS ProjectId
            FROM branches_in_merge_group bmg
            INNER JOIN branch_in_project bp ON bp.id = bmg.branch_in_project_id
            WHERE bmg.merge_group_id = ANY(@Ids)
            """,
            new { Ids = mergeGroupIds.ToArray() });

        var result = new Dictionary<int, HashSet<int>>();
        foreach (var (mgId, projId) in rows)
        {
            if (!result.TryGetValue(mgId, out var set))
            {
                set = [];
                result[mgId] = set;
            }

            set.Add(projId);
        }

        return result;
    }

    /// <summary>
    ///     Finds connected components in the merge-group graph where edges exist between
    ///     groups that share at least one project.  Returns one list per component, preserving
    ///     the original entry order within each component.
    /// </summary>
    private static List<List<MergeQueueEntryInfo>> FindConnectedComponents(
        IReadOnlyList<MergeQueueEntryInfo> entries,
        Dictionary<int, HashSet<int>> projectsPerGroup)
    {
        var parent = entries.Select((e, i) => i).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b)
                parent[a] = b;
        }

        // Union entries that share at least one project.
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = i + 1; j < entries.Count; j++)
            {
                var pi = projectsPerGroup.GetValueOrDefault(entries[i].MergeGroupId, []);
                var pj = projectsPerGroup.GetValueOrDefault(entries[j].MergeGroupId, []);
                if (pi.Overlaps(pj))
                    Union(i, j);
            }
        }

        return entries
            .Select((e, i) => (entry: e, root: Find(i)))
            .GroupBy(x => x.root)
            .Select(g => g.Select(x => x.entry).ToList())
            .ToList();
    }

    /// <summary>
    ///     Round-robin interleaves multiple ordered lists, preserving the original order within
    ///     each source list.  E.g. [A,B,C] and [D,E] → [A,D,B,E,C].
    /// </summary>
    private static List<T> Interleave<T>(IReadOnlyList<List<T>> lists)
    {
        var result = new List<T>();
        var maxLen = lists.Max(l => l.Count);

        for (var i = 0; i < maxLen; i++)
        {
            foreach (var list in lists)
            {
                if (i < list.Count)
                    result.Add(list[i]);
            }
        }

        return result;
    }

    private void DeleteEmptyQueue(IDbConnection connection, int queueId)
    {
        connection.Execute("DELETE FROM merge_queue WHERE id = @QueueId", new { QueueId = queueId });
        _logger.LogInformation("MergeQueueRepository: deleted empty queue {QueueId}", queueId);
    }
}
