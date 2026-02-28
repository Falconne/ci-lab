using Dapper;
using Mergician.Entities;
using Mergician.Entities.Database;
using Mergician.Services.Time;
using System.Data;

namespace Mergician.Services.Database;

/// <summary>
///     Dapper-based implementation of merge-group and branch operations.
///     All timestamps are stored and returned in UTC.
///     Uses INSERT ... ON CONFLICT for thread-safe upserts.
/// </summary>
public class MergeGroupRepository : IMergeGroupRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly ILogger<MergeGroupRepository> _logger;

    public MergeGroupRepository(IDbConnectionFactory connectionFactory, ILogger<MergeGroupRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public BranchInProjectRecord GetOrCreateBranchRecord(string branchName, int projectId, string projectName)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var record = connection.QueryFirstOrDefault<BranchInProjectRecord>(
            """
            INSERT INTO branch_in_project (branch_name, project_id, project_name)
            VALUES (@BranchName, @ProjectId, @ProjectName)
            ON CONFLICT (branch_name, project_id)
            DO UPDATE SET project_name = EXCLUDED.project_name
            RETURNING id, branch_name AS BranchName, project_id AS ProjectId, project_name AS ProjectName
            """,
            new { BranchName = branchName, ProjectId = projectId, ProjectName = projectName });

        if (record == null)
        {
            throw new InvalidOperationException(
                $"Failed to get or create branch '{branchName}' in project {projectId}");
        }

        _logger.LogDebug(
            "Got/created branch record {Id} for '{BranchName}' in project {ProjectId}",
            record.Id,
            branchName,
            projectId);

        return record;
    }

    // TODO: Make this method return `MergeGroup` (and update the query to include associated branches). There
    // is no need to `MergeGroup` so replace all usage of it with `GetMergeGroup`.
    public MergeGroupRecord GetOrCreateMergeGroup(string name)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var record = connection.QueryFirstOrDefault<MergeGroupRecord>(
            """
            INSERT INTO merge_group (name, last_update_time)
            VALUES (@Name, @Now)
            ON CONFLICT ON CONSTRAINT uq_merge_group_name
            DO UPDATE SET name = EXCLUDED.name
            RETURNING id AS Id, name AS Name, last_update_time AS LastUpdateTime
            """,
            new { Name = name, Now = DateTimeOffset.UtcNow });

        if (record == null)
        {
            throw new InvalidOperationException($"Failed to get or create merge group '{name}'");
        }

        _logger.LogDebug("Got/created merge group {Id} with name '{Name}'", record.Id, name);
        record.LastUpdateTime = UtcTimestamp.EnsureUtc(
            record.LastUpdateTime,
            () => $"MergeGroupRepository.GetOrCreateMergeGroup merge group {record.Id}",
            _logger);

        return record;
    }

    public void EnsureBranchInMergeGroup(int mergeGroupId, int branchInProjectId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        connection.Execute(
            """
            INSERT INTO branches_in_merge_group (merge_group_id, branch_in_project_id)
            VALUES (@MergeGroupId, @BranchInProjectId)
            ON CONFLICT (merge_group_id, branch_in_project_id) DO NOTHING
            """,
            new { MergeGroupId = mergeGroupId, BranchInProjectId = branchInProjectId });

        _logger.LogDebug(
            "Ensured branch {BranchId} is in merge group {MergeGroupId}",
            branchInProjectId,
            mergeGroupId);
    }

    public void EnsureUserInMergeGroup(int gitlabUserId, int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        connection.Execute(
            """
            INSERT INTO users_in_merge_groups (gitlab_user_id, merge_group_id)
            VALUES (@GitlabUserId, @MergeGroupId)
            ON CONFLICT (gitlab_user_id, merge_group_id) DO NOTHING
            """,
            new { GitlabUserId = gitlabUserId, MergeGroupId = mergeGroupId });

        _logger.LogDebug(
            "Ensured user {UserId} is in merge group {MergeGroupId}",
            gitlabUserId,
            mergeGroupId);
    }

    public void UpdateMergeGroupTimestamp(int mergeGroupId, DateTimeOffset lastUpdateTime)
    {
        var utcTimestamp = UtcTimestamp.EnsureUtc(
            lastUpdateTime,
            () => $"MergeGroupRepository.UpdateMergeGroupTimestamp merge group {mergeGroupId}",
            _logger);

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        connection.Execute(
            "UPDATE merge_group SET last_update_time = @Timestamp WHERE id = @Id",
            new { Id = mergeGroupId, Timestamp = utcTimestamp });

        _logger.LogDebug(
            "Updated merge group {MergeGroupId} timestamp to {Timestamp}",
            mergeGroupId,
            utcTimestamp);
    }

    public List<MergeGroup> GetMergeGroupsForUser(int gitlabUserId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var rows = connection.Query<BranchDataRow>(
                """
                SELECT
                    bp.id AS BranchInProjectId,
                    bp.branch_name AS BranchName,
                    bp.project_id AS ProjectId,
                    bp.project_name AS ProjectName,
                    mg.id AS MergeGroupId,
                    mg.name AS MergeGroupName,
                    mg.last_update_time AS LastUpdateTime,
                    bp.has_merge_request AS HasMergeRequest,
                    bp.merge_request_title AS MergeRequestTitle,
                    bp.merge_request_url AS MergeRequestUrl,
                    bp.project_url AS ProjectUrl,
                    bp.approvals_required AS ApprovalsRequired,
                    bp.approvals_given AS ApprovalsGiven
                FROM users_in_merge_groups umg
                INNER JOIN merge_group mg ON mg.id = umg.merge_group_id
                INNER JOIN branches_in_merge_group bmg ON bmg.merge_group_id = mg.id
                INNER JOIN branch_in_project bp ON bp.id = bmg.branch_in_project_id
                WHERE umg.gitlab_user_id = @GitlabUserId
                ORDER BY mg.last_update_time DESC, bp.branch_name, bp.project_name
                """,
                new { GitlabUserId = gitlabUserId })
            .ToList();

        foreach (var r in rows)
        {
            r.LastUpdateTime = UtcTimestamp.EnsureUtc(
                r.LastUpdateTime,
                () => $"MergeGroupRepository.GetMergeGroupsForUser merge group {r.MergeGroupId}",
                _logger);
        }

        AttachBuildJobs(connection, rows);

        // Group flat rows into MergeGroup objects, preserving ORDER BY merge group ordering
        var groupOrder = new List<int>();
        var groupNames = new Dictionary<int, string>();
        var groupTimes = new Dictionary<int, DateTimeOffset>();
        var groupBranches = new Dictionary<int, List<BranchRecord>>();

        foreach (var row in rows)
        {
            if (!groupBranches.ContainsKey(row.MergeGroupId))
            {
                groupOrder.Add(row.MergeGroupId);
                groupNames[row.MergeGroupId] = row.MergeGroupName;
                groupTimes[row.MergeGroupId] = row.LastUpdateTime;
                groupBranches[row.MergeGroupId] = [];
            }

            groupBranches[row.MergeGroupId].Add(ToBranchRecord(row));
        }

        var result = groupOrder
            .Select(id => new MergeGroup(id, groupNames[id], groupTimes[id], groupBranches[id]))
            .ToList();

        _logger.LogDebug(
            "Retrieved {GroupCount} merge groups with {BranchCount} branches for user {UserId}",
            result.Count,
            rows.Count,
            gitlabUserId);

        return result;
    }

    public MergeGroup? GetMergeGroup(int gitlabUserId, int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var rows = connection.Query<BranchDataRow>(
                """
                SELECT
                    bp.id AS BranchInProjectId,
                    bp.branch_name AS BranchName,
                    bp.project_id AS ProjectId,
                    bp.project_name AS ProjectName,
                    mg.id AS MergeGroupId,
                    mg.name AS MergeGroupName,
                    mg.last_update_time AS LastUpdateTime,
                    bp.has_merge_request AS HasMergeRequest,
                    bp.merge_request_title AS MergeRequestTitle,
                    bp.merge_request_url AS MergeRequestUrl,
                    bp.project_url AS ProjectUrl,
                    bp.approvals_required AS ApprovalsRequired,
                    bp.approvals_given AS ApprovalsGiven
                FROM users_in_merge_groups umg
                INNER JOIN merge_group mg ON mg.id = umg.merge_group_id
                INNER JOIN branches_in_merge_group bmg ON bmg.merge_group_id = mg.id
                INNER JOIN branch_in_project bp ON bp.id = bmg.branch_in_project_id
                WHERE umg.gitlab_user_id = @GitlabUserId
                  AND mg.id = @MergeGroupId
                ORDER BY bp.project_name, bp.branch_name
                """,
                new { GitlabUserId = gitlabUserId, MergeGroupId = mergeGroupId })
            .ToList();

        if (rows.Count == 0)
        {
            _logger.LogDebug(
                "No merge group found for user {UserId} with merge group {MergeGroupId}",
                gitlabUserId,
                mergeGroupId);

            return null;
        }

        foreach (var r in rows)
        {
            r.LastUpdateTime = UtcTimestamp.EnsureUtc(
                r.LastUpdateTime,
                () => $"MergeGroupRepository.GetMergeGroup merge group {r.MergeGroupId}",
                _logger);
        }

        AttachBuildJobs(connection, rows);

        _logger.LogDebug(
            "Retrieved {Count} branches for user {UserId} in merge group {MergeGroupId}",
            rows.Count,
            gitlabUserId,
            mergeGroupId);

        var first = rows[0];
        return new MergeGroup(
            first.MergeGroupId,
            first.MergeGroupName,
            first.LastUpdateTime,
            rows.Select(ToBranchRecord).ToList());
    }

    public void DeleteBranch(int branchInProjectId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        using var transaction = connection.BeginTransaction();

        connection.Execute(
            "DELETE FROM branches_in_merge_group WHERE branch_in_project_id = @Id",
            new { Id = branchInProjectId },
            transaction);

        connection.Execute(
            "DELETE FROM branch_in_project WHERE id = @Id",
            new { Id = branchInProjectId },
            transaction);

        transaction.Commit();

        _logger.LogInformation(
            "Deleted branch record {BranchId} and its merge group associations",
            branchInProjectId);
    }

    public void DeleteMergeGroup(int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        connection.Execute(
            "DELETE FROM merge_group WHERE id = @Id",
            new { Id = mergeGroupId });

        _logger.LogInformation("Deleted merge group {MergeGroupId} and all its associations", mergeGroupId);
    }

    public List<MergeGroupRecord> GetEmptyMergeGroups()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var results = connection.Query<MergeGroupRecord>(
                """
                SELECT mg.id AS Id, mg.name AS Name, mg.last_update_time AS LastUpdateTime
                FROM merge_group mg
                LEFT JOIN branches_in_merge_group bmg ON bmg.merge_group_id = mg.id
                WHERE bmg.id IS NULL
                """)
            .ToList();

        foreach (var r in results)
        {
            r.LastUpdateTime = UtcTimestamp.EnsureUtc(
                r.LastUpdateTime,
                () => $"MergeGroupRepository.GetEmptyMergeGroups result merge group {r.Id}",
                _logger);
        }

        return results;
    }

    public List<BranchInProjectRecord> GetAllBranches()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return connection.Query<BranchInProjectRecord>(
                "SELECT id AS Id, branch_name AS BranchName, project_id AS ProjectId, project_name AS ProjectName FROM branch_in_project")
            .ToList();
    }

    public BranchInProjectRecord? GetBranchRecord(string branchName, int projectId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return connection.QueryFirstOrDefault<BranchInProjectRecord>(
            "SELECT id AS Id, branch_name AS BranchName, project_id AS ProjectId, project_name AS ProjectName FROM branch_in_project WHERE branch_name = @BranchName AND project_id = @ProjectId",
            new { BranchName = branchName, ProjectId = projectId });
    }

    public List<int> GetMergeGroupIdsForBranch(int branchInProjectId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return connection.Query<int>(
                "SELECT merge_group_id FROM branches_in_merge_group WHERE branch_in_project_id = @Id",
                new { Id = branchInProjectId })
            .ToList();
    }

    public void UpdateBranchDetails(
        int branchInProjectId,
        bool hasMergeRequest,
        string? mergeRequestTitle,
        string? mergeRequestUrl,
        string? projectUrl,
        int? approvalsRequired,
        int? approvalsGiven,
        List<BranchBuildJob> buildJobs)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        using var transaction = connection.BeginTransaction();

        connection.Execute(
            """
            UPDATE branch_in_project
            SET has_merge_request   = @HasMergeRequest,
                merge_request_title = @MergeRequestTitle,
                merge_request_url   = @MergeRequestUrl,
                project_url         = @ProjectUrl,
                approvals_required  = @ApprovalsRequired,
                approvals_given     = @ApprovalsGiven
            WHERE id = @BranchInProjectId
            """,
            new
            {
                BranchInProjectId = branchInProjectId,
                HasMergeRequest = hasMergeRequest,
                MergeRequestTitle = mergeRequestTitle,
                MergeRequestUrl = mergeRequestUrl,
                ProjectUrl = projectUrl,
                ApprovalsRequired = approvalsRequired,
                ApprovalsGiven = approvalsGiven
            },
            transaction);

        connection.Execute(
            "DELETE FROM branch_build_jobs WHERE branch_in_project_id = @Id",
            new { Id = branchInProjectId },
            transaction);

        if (buildJobs.Count > 0)
        {
            connection.Execute(
                """
                INSERT INTO branch_build_jobs (branch_in_project_id, name, status, url)
                VALUES (@BranchInProjectId, @Name, @Status, @Url)
                ON CONFLICT (branch_in_project_id, name) DO UPDATE SET status = EXCLUDED.status, url = EXCLUDED.url
                """,
                buildJobs.Select(j => new { BranchInProjectId = branchInProjectId, j.Name, j.Status, j.Url }),
                transaction);
        }

        transaction.Commit();

        _logger.LogDebug(
            "Updated branch {BranchInProjectId} details: hasMr={HasMr}, approvals={Given}/{Required}, {JobCount} build jobs",
            branchInProjectId,
            hasMergeRequest,
            approvalsGiven,
            approvalsRequired,
            buildJobs.Count);
    }

    private void AttachBuildJobs(IDbConnection connection, List<BranchDataRow> branches)
    {
        if (branches.Count == 0)
        {
            return;
        }

        var ids = branches.Select(b => b.BranchInProjectId).Distinct().ToArray();

        var jobs = connection.Query<(int BranchInProjectId, string Name, string Status, string? Url)>(
                """
                SELECT branch_in_project_id AS BranchInProjectId, name AS Name, status AS Status, url AS Url
                FROM branch_build_jobs
                WHERE branch_in_project_id = ANY(@Ids)
                """,
                new { Ids = ids })
            .GroupBy(j => j.BranchInProjectId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(j => new BranchBuildJob(j.Name, j.Status, j.Url)).ToList());

        foreach (var branch in branches)
        {
            if (jobs.TryGetValue(branch.BranchInProjectId, out var branchJobs))
            {
                branch.BuildJobs = branchJobs;
            }
        }
    }

    /// <summary>
    ///     Converts a <see cref="BranchDataRow" /> SQL result into a <see cref="BranchRecord" /> response object.
    ///     Extracts the short project display name from the stored NameWithNamespace.
    /// </summary>
    private static BranchRecord ToBranchRecord(BranchDataRow row)
    {
        var nameWithNamespace = row.ProjectName;
        var trimmed = nameWithNamespace.Trim();
        var lastSlash = trimmed.LastIndexOf('/');
        var displayName = lastSlash >= 0 && lastSlash < trimmed.Length - 1
            ? trimmed[(lastSlash + 1)..].Trim()
            : trimmed;

        return new BranchRecord(
            row.BranchName,
            row.ProjectId,
            displayName,
            nameWithNamespace,
            row.HasMergeRequest,
            row.ApprovalsRequired,
            row.ApprovalsGiven,
            row.LastUpdateTime,
            row.MergeRequestTitle,
            row.MergeRequestUrl,
            row.ProjectUrl,
            row.BuildJobs,
            row.BranchInProjectId);
    }
}