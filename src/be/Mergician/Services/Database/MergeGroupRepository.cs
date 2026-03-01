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

    public BranchInProjectRecord GetOrCreateBranchRecord(
        string branchName,
        int projectId,
        string projectName,
        string projectDisplayName)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var record = connection.QueryFirstOrDefault<BranchInProjectRecord>(
            """
            INSERT INTO branch_in_project (branch_name, project_id, project_name, project_display_name)
            VALUES (@BranchName, @ProjectId, @ProjectName, @ProjectDisplayName)
            ON CONFLICT (branch_name, project_id)
            DO UPDATE SET project_name = EXCLUDED.project_name, project_display_name = EXCLUDED.project_display_name
            RETURNING id, branch_name AS BranchName, project_id AS ProjectId, project_name AS ProjectName
            """,
            new { BranchName = branchName, ProjectId = projectId, ProjectName = projectName, ProjectDisplayName = projectDisplayName });

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

    public MergeGroup GetOrCreateMergeGroup(string name)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var record = connection.QueryFirstOrDefault<MergeGroupBase>(
            """
            INSERT INTO merge_group (name)
            VALUES (@Name)
            ON CONFLICT ON CONSTRAINT uq_merge_group_name
            DO UPDATE SET name = EXCLUDED.name
            RETURNING id AS Id, name AS Name
            """,
            new { Name = name });

        if (record == null)
        {
            throw new InvalidOperationException($"Failed to get or create merge group '{name}'");
        }

        _logger.LogDebug("Got/created merge group {Id} with name '{Name}'", record.Id, name);

        return GetMergeGroupFor(connection, record);
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
                    bp.project_display_name AS ProjectDisplayName,
                    mg.id AS MergeGroupId,
                    mg.name AS MergeGroupName,
                    bp.last_update_time AS LastUpdateTime,
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
                ORDER BY bp.project_name, bp.branch_name
                """,
                new { GitlabUserId = gitlabUserId })
            .ToList();

        foreach (var r in rows)
        {
            if (r.LastUpdateTime.HasValue)
            {
                r.LastUpdateTime = UtcTimestamp.EnsureUtc(
                    r.LastUpdateTime.Value,
                    () =>
                        $"MergeGroupRepository.GetMergeGroupsForUser group {r.MergeGroupId} branch {r.BranchInProjectId}",
                    _logger);
            }
        }

        AttachBuildJobs(connection, rows);

        // Group flat rows into MergeGroup objects
        var groupOrder = new List<int>();
        var groupNames = new Dictionary<int, string>();
        var groupBranches = new Dictionary<int, List<BranchRecord>>();

        foreach (var row in rows)
        {
            if (!groupBranches.ContainsKey(row.MergeGroupId))
            {
                groupOrder.Add(row.MergeGroupId);
                groupNames[row.MergeGroupId] = row.MergeGroupName;
                groupBranches[row.MergeGroupId] = [];
            }

            groupBranches[row.MergeGroupId].Add(ToBranchRecord(row));
        }

        var result = groupOrder
            .Select(id => new MergeGroup(id, groupNames[id], groupBranches[id]))
            .ToList();

        _logger.LogDebug(
            "Retrieved {GroupCount} merge groups with {BranchCount} branches for user {UserId}",
            result.Count,
            rows.Count,
            gitlabUserId);

        return result;
    }

    public MergeGroup? GetMergeGroup(int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var record = connection.QueryFirstOrDefault<MergeGroupBase>(
            """
            SELECT id AS Id, name AS Name
            FROM merge_group
            WHERE id = @MergeGroupId
            """,
            new { MergeGroupId = mergeGroupId });

        if (record != null)
        {
            return GetMergeGroupFor(connection, record);
        }

        _logger.LogDebug("No merge group found with id {MergeGroupId}", mergeGroupId);
        return null;
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

    public List<MergeGroupBase> GetEmptyMergeGroups()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return connection.Query<MergeGroupBase>(
                """
                SELECT mg.id AS Id, mg.name AS Name
                FROM merge_group mg
                LEFT JOIN branches_in_merge_group bmg ON bmg.merge_group_id = mg.id
                WHERE bmg.id IS NULL
                """)
            .ToList();
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
        List<BranchBuildJob> buildJobs,
        DateTimeOffset? lastCommitTime = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        using var transaction = connection.BeginTransaction();

        // If a commit time is provided, convert to UTC and include it in the update
        DateTimeOffset? utcCommitTime = null;
        if (lastCommitTime.HasValue)
        {
            utcCommitTime = UtcTimestamp.EnsureUtc(
                lastCommitTime.Value,
                () => $"MergeGroupRepository.UpdateBranchDetails branch {branchInProjectId}",
                _logger);
        }

        connection.Execute(
            """
            UPDATE branch_in_project
            SET has_merge_request   = @HasMergeRequest,
                merge_request_title = @MergeRequestTitle,
                merge_request_url   = @MergeRequestUrl,
                project_url         = @ProjectUrl,
                approvals_required  = @ApprovalsRequired,
                approvals_given     = @ApprovalsGiven,
                last_update_time    = COALESCE(@LastCommitTime, last_update_time)
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
                ApprovalsGiven = approvalsGiven,
                LastCommitTime = utcCommitTime
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
            "Updated branch {BranchInProjectId} details: hasMr={HasMr}, approvals={Given}/{Required}, {JobCount} build jobs, commitTime={CommitTime}",
            branchInProjectId,
            hasMergeRequest,
            approvalsGiven,
            approvalsRequired,
            buildJobs.Count,
            utcCommitTime);
    }

    private MergeGroup GetMergeGroupFor(IDbConnection connection, MergeGroupBase record)
    {
        var rows = connection.Query<BranchDataRow>(
                """
                SELECT
                    bp.id AS BranchInProjectId,
                    bp.branch_name AS BranchName,
                    bp.project_id AS ProjectId,
                    bp.project_name AS ProjectName,
                    bp.project_display_name AS ProjectDisplayName,
                    mg.id AS MergeGroupId,
                    mg.name AS MergeGroupName,
                    bp.last_update_time AS LastUpdateTime,
                    bp.has_merge_request AS HasMergeRequest,
                    bp.merge_request_title AS MergeRequestTitle,
                    bp.merge_request_url AS MergeRequestUrl,
                    bp.project_url AS ProjectUrl,
                    bp.approvals_required AS ApprovalsRequired,
                    bp.approvals_given AS ApprovalsGiven
                FROM branches_in_merge_group bmg
                INNER JOIN branch_in_project bp ON bp.id = bmg.branch_in_project_id
                INNER JOIN merge_group mg ON mg.id = bmg.merge_group_id
                WHERE bmg.merge_group_id = @MergeGroupId
                ORDER BY bp.project_name, bp.branch_name
                """,
                new { MergeGroupId = record.Id })
            .ToList();

        foreach (var r in rows)
        {
            if (r.LastUpdateTime.HasValue)
            {
                r.LastUpdateTime = UtcTimestamp.EnsureUtc(
                    r.LastUpdateTime.Value,
                    () =>
                        $"MergeGroupRepository.GetMergeGroupById group {r.MergeGroupId} branch {r.BranchInProjectId}",
                    _logger);
            }
        }

        AttachBuildJobs(connection, rows);

        return new MergeGroup(
            record.Id,
            record.Name,
            rows.Select(ToBranchRecord).ToList());
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
    /// </summary>
    private static BranchRecord ToBranchRecord(BranchDataRow row)
    {
        var nameWithNamespace = row.ProjectName;

        // Use the stored display name. Fall back to parsing the namespace if not yet populated.
        var displayName = row.ProjectDisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            var trimmed = nameWithNamespace.Trim();
            var lastSlash = trimmed.LastIndexOf('/');
            displayName = lastSlash >= 0 && lastSlash < trimmed.Length - 1
                ? trimmed[(lastSlash + 1)..].Trim()
                : trimmed;
        }

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