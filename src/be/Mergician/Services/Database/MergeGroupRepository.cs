using System.Data;
using Dapper;
using Mergician.Entities;
using Mergician.Entities.Database;
using Mergician.Services.Time;

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

    public BranchInProject GetOrCreateBranchRecord(string branchName, GitLabProject project)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var record = connection.QueryFirstOrDefault<BranchInProject>(
            """
            INSERT INTO branch_in_project (branch_name, project_id, project_name, project_name_with_namespace)
            VALUES (@BranchName, @ProjectId, @ProjectName, @ProjectNameWithNamespace)
            ON CONFLICT (branch_name, project_id)
            DO UPDATE SET project_name = EXCLUDED.project_name, project_name_with_namespace = EXCLUDED.project_name_with_namespace
            RETURNING id, branch_name AS BranchName, project_id AS ProjectId, project_name AS ProjectName,
                      project_name_with_namespace AS ProjectNameWithNamespace
            """,
            new
            {
                BranchName = branchName,
                ProjectId = project.Id,
                ProjectName = project.Name,
                ProjectNameWithNamespace = project.NameWithNamespace
            });

        if (record == null)
        {
            throw new InvalidOperationException(
                $"Failed to get or create branch '{branchName}' in project {project.Id}");
        }

        _logger.LogDebug(
            "Got/created branch record {Id} for '{BranchName}' in project {ProjectId}",
            record.Id,
            branchName,
            project.Id);

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
            DO NOTHING
            RETURNING id AS Id, name AS Name, auto_merge AS AutoMerge, auto_rebase AS AutoRebase, auto_merge_warning AS AutoMergeWarning
            """,
            new { Name = name })
            ?? connection.QueryFirstOrDefault<MergeGroupBase>(
                """
                SELECT id AS Id, name AS Name, auto_merge AS AutoMerge, auto_rebase AS AutoRebase, auto_merge_warning AS AutoMergeWarning
                FROM merge_group
                WHERE name = @Name
                """,
                new { Name = name });

        if (record == null)
        {
            throw new InvalidOperationException($"Failed to get or create merge group '{name}'");
        }

        _logger.LogDebug("Got/created merge group {Id} with name '{Name}'", record.Id, name);

        return GetBranchesFor(connection, record);
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

        var records = connection.Query<MergeGroupBase>(
                """
                SELECT mg.id AS Id, mg.name AS Name, mg.auto_merge AS AutoMerge, mg.auto_rebase AS AutoRebase, mg.auto_merge_warning AS AutoMergeWarning
                FROM users_in_merge_groups umg
                INNER JOIN merge_group mg ON mg.id = umg.merge_group_id
                WHERE umg.gitlab_user_id = @GitlabUserId
                """,
                new { GitlabUserId = gitlabUserId })
            .ToList();

        var result = GetBranchesForGroups(connection, records);

        _logger.LogDebug(
            "Retrieved {GroupCount} merge groups with {BranchCount} branches for user {UserId}",
            result.Count,
            result.Sum(g => g.Branches.Count),
            gitlabUserId);

        return result;
    }

    public MergeGroup? GetMergeGroup(int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var record = connection.QueryFirstOrDefault<MergeGroupBase>(
            """
            SELECT id AS Id, name AS Name, auto_merge AS AutoMerge, auto_rebase AS AutoRebase, auto_merge_warning AS AutoMergeWarning
            FROM merge_group
            WHERE id = @MergeGroupId
            """,
            new { MergeGroupId = mergeGroupId });

        if (record != null)
        {
            return GetBranchesFor(connection, record);
        }

        _logger.LogDebug("No merge group found with id {MergeGroupId}", mergeGroupId);
        return null;
    }

    public void RemoveBranch(int branchInProjectId)
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
            "Removed branch record {BranchId} and its merge group associations",
            branchInProjectId);
    }

    public void RemoveMergeGroup(int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        connection.Execute(
            "DELETE FROM merge_group WHERE id = @Id",
            new { Id = mergeGroupId });

        _logger.LogInformation("Removed merge group {MergeGroupId} and all its associations", mergeGroupId);
    }

    public void CleanupEmptyMergeGroups()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var deletedGroups = connection.Query<MergeGroupBase>(
                """
                DELETE FROM merge_group
                WHERE id NOT IN (SELECT DISTINCT merge_group_id FROM branches_in_merge_group)
                RETURNING id AS Id, name AS Name, auto_merge AS AutoMerge, auto_rebase AS AutoRebase, auto_merge_warning AS AutoMergeWarning
                """)
            .ToList();

        foreach (var group in deletedGroups)
        {
            _logger.LogInformation(
                "Removed empty merge group {MergeGroupId} '{Name}'",
                group.Id,
                group.Name);
        }
    }

    public List<MergeGroupBase> GetEmptyMergeGroups()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return connection.Query<MergeGroupBase>(
                """
                SELECT mg.id AS Id, mg.name AS Name, mg.auto_merge AS AutoMerge, mg.auto_rebase AS AutoRebase, mg.auto_merge_warning AS AutoMergeWarning
                FROM merge_group mg
                LEFT JOIN branches_in_merge_group bmg ON bmg.merge_group_id = mg.id
                WHERE bmg.id IS NULL
                """)
            .ToList();
    }

    public List<BranchInProject> GetAllBranches()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        return connection.Query<BranchInProject>(
                "SELECT id AS Id, branch_name AS BranchName, project_id AS ProjectId, project_name AS ProjectName FROM branch_in_project")
            .ToList();
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

    public void UpdateBranchDetails(int branchInProjectId, BranchDetailsUpdate update)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        using var transaction = connection.BeginTransaction();

        // If a commit time is provided, convert to UTC and include it in the update
        DateTimeOffset? utcCommitTime = null;
        if (update.LastCommitTime.HasValue)
        {
            utcCommitTime = UtcTimestamp.EnsureUtc(
                update.LastCommitTime.Value,
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
                needs_rebase        = @NeedsRebase,
                mr_status           = @MRStatus,
                mr_status_reasons   = @MRStatusReasons,
                last_commit_message = @LastCommitMessage,
                last_update_time    = COALESCE(@LastCommitTime, last_update_time)
            WHERE id = @BranchInProjectId
            """,
            new
            {
                BranchInProjectId = branchInProjectId,
                update.HasMergeRequest,
                update.MergeRequestTitle,
                update.MergeRequestUrl,
                update.ProjectUrl,
                update.ApprovalsRequired,
                update.ApprovalsGiven,
                update.NeedsRebase,
                MRStatus = update.MrStatus,
                MRStatusReasons = update.MrStatusReasons,
                update.LastCommitMessage,
                LastCommitTime = utcCommitTime
            },
            transaction);

        connection.Execute(
            "DELETE FROM branch_build_jobs WHERE branch_in_project_id = @Id",
            new { Id = branchInProjectId },
            transaction);

        if (update.BuildJobs.Count > 0)
        {
            connection.Execute(
                """
                INSERT INTO branch_build_jobs (branch_in_project_id, name, status, url)
                VALUES (@BranchInProjectId, @Name, @Status, @Url)
                ON CONFLICT (branch_in_project_id, name) DO UPDATE SET status = EXCLUDED.status, url = EXCLUDED.url
                """,
                update.BuildJobs.Select(j => new { BranchInProjectId = branchInProjectId, j.Name, j.Status, j.Url }),
                transaction);
        }

        transaction.Commit();

        _logger.LogDebug(
            "Updated branch {BranchInProjectId} details: hasMergeRequest={HasMergeRequest}, approvals={Given}/{Required}, needsRebase={NeedsRebase}, {JobCount} build jobs, commitTime={CommitTime}",
            branchInProjectId,
            update.HasMergeRequest,
            update.ApprovalsGiven,
            update.ApprovalsRequired,
            update.NeedsRebase,
            update.BuildJobs.Count,
            utcCommitTime);
    }

    public int UpdateAutoMergeSettings(int mergeGroupId, bool autoMerge, bool autoRebase)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var rowsAffected = connection.Execute(
            """
            UPDATE merge_group
            SET auto_merge = @AutoMerge,
                auto_rebase = @AutoRebase
            WHERE id = @MergeGroupId
            """,
            new { MergeGroupId = mergeGroupId, AutoMerge = autoMerge, AutoRebase = autoRebase });

        _logger.LogInformation(
            "Updated auto merge settings for merge group {MergeGroupId}: autoMerge={AutoMerge}, autoRebase={AutoRebase}",
            mergeGroupId,
            autoMerge,
            autoRebase);

        return rowsAffected;
    }

    public List<MergeGroup> GetMergeGroupsWithAutoSettings()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var records = connection.Query<MergeGroupBase>(
                """
                SELECT id AS Id, name AS Name, auto_merge AS AutoMerge, auto_rebase AS AutoRebase, auto_merge_warning AS AutoMergeWarning
                FROM merge_group
                WHERE auto_merge = TRUE OR auto_rebase = TRUE
                """)
            .ToList();

        var result = GetBranchesForGroups(connection, records);

        _logger.LogDebug(
            "Retrieved {Count} merge groups with auto settings enabled",
            result.Count);

        return result;
    }

    public void UpdateAutoMergeWarning(int mergeGroupId, string? warning)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        connection.Execute(
            """
            UPDATE merge_group
            SET auto_merge_warning = @Warning
            WHERE id = @MergeGroupId
            """,
            new { MergeGroupId = mergeGroupId, Warning = warning });

        if (warning != null)
        {
            _logger.LogInformation(
                "Set auto merge warning for merge group {MergeGroupId}: {Warning}",
                mergeGroupId,
                warning);
        }
        else
        {
            _logger.LogDebug("Cleared auto merge warning for merge group {MergeGroupId}", mergeGroupId);
        }
    }

    public bool IsUserInMergeGroup(int gitlabUserId, int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var exists = connection.QueryFirstOrDefault<int?>(
            """
            SELECT 1
            FROM users_in_merge_groups
            WHERE gitlab_user_id = @GitlabUserId AND merge_group_id = @MergeGroupId
            """,
            new { GitlabUserId = gitlabUserId, MergeGroupId = mergeGroupId });

        return exists != null;
    }

    public void RemoveUserFromMergeGroup(int gitlabUserId, int mergeGroupId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        connection.Execute(
            """
            DELETE FROM users_in_merge_groups
            WHERE gitlab_user_id = @GitlabUserId AND merge_group_id = @MergeGroupId
            """,
            new { GitlabUserId = gitlabUserId, MergeGroupId = mergeGroupId });

        _logger.LogInformation(
            "Removed user {UserId} from merge group {MergeGroupId}",
            gitlabUserId,
            mergeGroupId);
    }

    public MergeGroup? FindMergeGroupByBranch(string branchName, int projectId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var record = connection.QueryFirstOrDefault<MergeGroupBase>(
            """
            SELECT mg.id AS Id, mg.name AS Name, mg.auto_merge AS AutoMerge, mg.auto_rebase AS AutoRebase, mg.auto_merge_warning AS AutoMergeWarning
            FROM merge_group mg
            INNER JOIN branches_in_merge_group bmg ON bmg.merge_group_id = mg.id
            INNER JOIN branch_in_project bp ON bp.id = bmg.branch_in_project_id
            WHERE bp.branch_name = @BranchName AND bp.project_id = @ProjectId
            LIMIT 1
            """,
            new { BranchName = branchName, ProjectId = projectId });

        if (record == null)
        {
            _logger.LogDebug(
                "No merge group found containing branch '{BranchName}' in project {ProjectId}",
                branchName,
                projectId);

            return null;
        }

        _logger.LogDebug(
            "Found merge group {MergeGroupId} for branch '{BranchName}' in project {ProjectId}",
            record.Id,
            branchName,
            projectId);

        return GetBranchesFor(connection, record);
    }

    private MergeGroup GetBranchesFor(IDbConnection connection, MergeGroupBase record)
    {
        return GetBranchesForGroups(connection, [record])[0];
    }

    /// <summary>
    ///     Fetches branches and build jobs for multiple merge groups in two queries (no N+1).
    ///     Preserves the ordering of <paramref name="records" />.
    /// </summary>
    private static List<MergeGroup> GetBranchesForGroups(
        IDbConnection connection,
        IReadOnlyList<MergeGroupBase> records)
    {
        if (records.Count == 0)
            return [];

        var groupIds = records.Select(r => r.Id).ToArray();

        // Single query for all branches across all groups; MergeGroupId is the first column
        // so Dapper's two-type multi-map can split on "Id" to produce (int, BranchWithActivity).
        var rows = connection.Query<int, BranchWithActivity, (int MergeGroupId, BranchWithActivity Branch)>(
                """
                SELECT
                    bmg.merge_group_id AS MergeGroupId,
                    bp.id AS Id,
                    bp.branch_name AS BranchName,
                    bp.project_id AS ProjectId,
                    bp.project_name AS ProjectName,
                    bp.project_name_with_namespace AS ProjectNameWithNamespace,
                    bp.has_merge_request AS HasMergeRequest,
                    bp.approvals_required AS ApprovalsRequired,
                    bp.approvals_given AS ApprovalsGiven,
                    bp.last_update_time AS LastUpdated,
                    bp.merge_request_title AS MergeRequestTitle,
                    bp.merge_request_url AS MergeRequestUrl,
                    bp.project_url AS ProjectUrl,
                    bp.needs_rebase AS NeedsRebase,
                    bp.mr_status AS MRStatus,
                    bp.mr_status_reasons AS MRStatusReasonsJson,
                    bp.last_commit_message AS LastCommitMessage
                FROM branches_in_merge_group bmg
                INNER JOIN branch_in_project bp ON bp.id = bmg.branch_in_project_id
                WHERE bmg.merge_group_id = ANY(@GroupIds)
                ORDER BY bp.project_name, bp.branch_name
                """,
                (groupId, branch) => (groupId, branch),
                new { GroupIds = groupIds },
                splitOn: "Id")
            .ToList();

        var branchesByGroup = rows
            .GroupBy(r => r.MergeGroupId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Branch).ToList());

        var allBranches = branchesByGroup.Values.SelectMany(b => b).ToList();
        var buildJobsMap = FetchBuildJobsMap(connection, allBranches.Select(b => b.Id).ToArray());
        foreach (var branchList in branchesByGroup.Values)
            ApplyBuildJobs(branchList, buildJobsMap);

        return records
            .Select(r => new MergeGroup(r.Id, r.Name, branchesByGroup.GetValueOrDefault(r.Id, []))
            {
                AutoMerge = r.AutoMerge,
                AutoRebase = r.AutoRebase,
                AutoMergeWarning = r.AutoMergeWarning
            })
            .ToList();
    }

    /// <summary>Fetches a map of branch ID to build jobs from the database.</summary>
    private static Dictionary<int, List<BranchBuildJob>> FetchBuildJobsMap(
        IDbConnection connection,
        int[] branchIds)
    {
        if (branchIds.Length == 0)
            return new Dictionary<int, List<BranchBuildJob>>();

        return connection.Query<(int BranchInProjectId, string Name, string Status, string? Url)>(
                """
                SELECT branch_in_project_id AS BranchInProjectId, name AS Name, status AS Status, url AS Url
                FROM branch_build_jobs
                WHERE branch_in_project_id = ANY(@Ids)
                """,
                new { Ids = branchIds })
            .GroupBy(j => j.BranchInProjectId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(j => new BranchBuildJob(j.Name, j.Status, j.Url)).ToList());
    }

    /// <summary>Applies a pre-fetched build jobs map to a list of branches.</summary>
    private static void ApplyBuildJobs(
        List<BranchWithActivity> branches,
        Dictionary<int, List<BranchBuildJob>> jobs)
    {
        for (var i = 0; i < branches.Count; i++)
        {
            var branch = branches[i];
            if (jobs.TryGetValue(branch.Id, out var branchJobs))
            {
                branches[i] = branch with { BuildJobs = branchJobs };
            }
        }
    }}
