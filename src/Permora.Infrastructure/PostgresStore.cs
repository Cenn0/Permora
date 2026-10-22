using System.Data;
using Npgsql;
using NpgsqlTypes;
using Permora.Core;

namespace Permora.Infrastructure;

public sealed class PostgresStore(NpgsqlDataSource source) : IPortalStore
{
    public const int SchemaVersion = 2;

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(72819451)", ct);
        await ExecuteAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_versions(version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", ct);
        await using var version = new NpgsqlCommand("SELECT COUNT(*) FROM schema_versions WHERE version = 1", connection, transaction);
        if (Convert.ToInt32(await version.ExecuteScalarAsync(ct)) == 0)
        {
            await using var stream = typeof(PostgresStore).Assembly.GetManifestResourceStream("Permora.Infrastructure.schema.sql")
                ?? throw new InvalidOperationException("Embedded schema is missing.");
            using var reader = new StreamReader(stream);
            await ExecuteAsync(connection, transaction, await reader.ReadToEndAsync(ct), ct);
            await ExecuteAsync(connection, transaction, "INSERT INTO schema_versions(version) VALUES (1)", ct);
        }
        await using var reviewScheduleVersion = new NpgsqlCommand("SELECT COUNT(*) FROM schema_versions WHERE version = 2", connection, transaction);
        if (Convert.ToInt32(await reviewScheduleVersion.ExecuteScalarAsync(ct)) == 0)
        {
            await using var stream = typeof(PostgresStore).Assembly.GetManifestResourceStream("Permora.Infrastructure.schema-review-schedules.sql")
                ?? throw new InvalidOperationException("Review schedule migration is missing.");
            using var reader = new StreamReader(stream);
            await ExecuteAsync(connection, transaction, await reader.ReadToEndAsync(ct), ct);
            await ExecuteAsync(connection, transaction, "INSERT INTO schema_versions(version) VALUES (2)", ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<DashboardFeed> DashboardAsync(IReadOnlyCollection<Guid> appIds, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        async Task<int> Count(string status)
        {
            await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM operations WHERE application_id=ANY(@apps) AND status=@status", connection);
            command.Parameters.AddWithValue("apps", NpgsqlDbType.Array | NpgsqlDbType.Uuid, appIds.ToArray());
            command.Parameters.AddWithValue("status", status);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }
        NpgsqlCommand Query(string sql)
        {
            var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("apps", NpgsqlDbType.Array | NpgsqlDbType.Uuid, appIds.ToArray());
            return command;
        }
        var pending = await Count("AwaitingApproval");
        var failed = await Count("Failed") + await Count("Conflict");
        var requests = await DocumentsAsync<Operation>(Query("SELECT document::text FROM operations WHERE application_id=ANY(@apps) AND status='AwaitingApproval' ORDER BY created_at DESC, id LIMIT 20"), ct);
        var recent = await DocumentsAsync<Operation>(Query("SELECT document::text FROM operations WHERE application_id=ANY(@apps) ORDER BY created_at DESC, id LIMIT 10"), ct);
        var audit = await DocumentsAsync<AuditEvent>(Query("SELECT document::text FROM audit_events WHERE application_id=ANY(@apps) ORDER BY (document->>'at')::timestamptz DESC, sequence DESC LIMIT 12"), ct);
        return new DashboardFeed(pending, failed, requests, recent, audit);
    }

    public async Task<IReadOnlyList<Team>> TeamsAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await DocumentsAsync<Team>(new NpgsqlCommand("SELECT document::text FROM teams ORDER BY id", connection), ct);
    }

    public async Task SaveTeamAsync(Team team, long? expectedRevision, Actor actor, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var sql = expectedRevision is null
            ? "INSERT INTO teams(id, revision, document) VALUES (@id, @revision, @document)"
            : "UPDATE teams SET revision=@revision, document=@document WHERE id=@id AND revision=@expected";
        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("id", team.Id);
        command.Parameters.AddWithValue("revision", team.Revision);
        command.Parameters.AddWithValue("expected", expectedRevision ?? -1);
        Json(command, "document", team);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new DomainException("revision_conflict", "The team was changed by another administrator.", 412);
        await using var audit = new NpgsqlCommand("INSERT INTO admin_audit(id, detail) VALUES (@id, @detail)", connection, tx);
        audit.Parameters.AddWithValue("id", Guid.NewGuid());
        Json(audit, "detail", new { action = "team.updated", actor = actor.Key, team.Id, team.Revision,
            previousRevision = expectedRevision, members = team.Members.Select(m => new { m.SubjectKey, m.Role }) });
        await audit.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ApplicationDocument>> ApplicationsAsync(IReadOnlyCollection<Guid> teams, bool all, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var command = new NpgsqlCommand("SELECT document::text FROM applications WHERE @all OR team_id = ANY(@teams) ORDER BY slug", connection);
        command.Parameters.AddWithValue("all", all);
        command.Parameters.AddWithValue("teams", NpgsqlDbType.Array | NpgsqlDbType.Uuid, teams.ToArray());
        return await DocumentsAsync<ApplicationDocument>(command, ct);
    }

    public async Task<ApplicationDocument?> ApplicationAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT document::text FROM applications WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id);
        return await OneAsync<ApplicationDocument>(command, ct);
    }

    public async Task CreateApplicationAsync(ApplicationDocument app, Actor actor, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var teamLock = new NpgsqlCommand("SELECT document::text FROM teams WHERE id=@team FOR SHARE", connection, tx);
        teamLock.Parameters.AddWithValue("team", app.TeamId);
        var team = await OneAsync<Team>(teamLock, ct) ?? throw new DomainException("not_found", "Team not found.", 404);
        if (!team.Members.Any(m => m.SubjectKey == actor.Key && m.Role == ManagementRole.Owner))
            throw new DomainException("owner_changed", "The registering account must still be a team owner.", 403);
        await using var command = new NpgsqlCommand("""
            INSERT INTO applications(id, team_id, realm_key, slug, environment, revision, document)
            VALUES (@id, @team, @realm, @slug, @environment, @revision, @document)
            """, connection, tx);
        command.Parameters.AddWithValue("id", app.Id); command.Parameters.AddWithValue("team", app.TeamId);
        command.Parameters.AddWithValue("realm", app.RealmKey); command.Parameters.AddWithValue("slug", app.Slug);
        command.Parameters.AddWithValue("environment", app.Environment); command.Parameters.AddWithValue("revision", app.Revision);
        Json(command, "document", app);
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        { throw new DomainException("already_exists", "This application already exists in the selected realm and environment.", 409); }
        await AppendAuditAsync(connection, tx, app.Id, actor, "application.registered",
            AuditDetails.ApplicationRegistered(app), DateTimeOffset.UtcNow, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<T> InApplicationAsync<T>(Guid id, Func<IApplicationTransaction, Task<T>> action, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ExecuteAsync(connection, transaction, "SET LOCAL lock_timeout = '5s'", ct);
        await using var load = new NpgsqlCommand("SELECT document::text FROM applications WHERE id=@id FOR UPDATE", connection, transaction);
        load.Parameters.AddWithValue("id", id);
        var app = await OneAsync<ApplicationDocument>(load, ct) ?? throw new DomainException("not_found", "Application not found.", 404);
        await using var teamCommand = new NpgsqlCommand("SELECT document::text FROM teams WHERE id=@id FOR SHARE", connection, transaction);
        teamCommand.Parameters.AddWithValue("id", app.TeamId);
        var team = await OneAsync<Team>(teamCommand, ct) ?? throw new InvalidDataException("Application owner missing.");
        var session = new Session(connection, transaction, app, team);
        var result = await action(session);
        if (session.Application != app)
        {
            if (session.Application.Id != app.Id || session.Application.Revision <= app.Revision)
                throw new InvalidOperationException("Invalid aggregate revision.");
            await using var update = new NpgsqlCommand("UPDATE applications SET document=@document, revision=@revision, team_id=@team WHERE id=@id", connection, transaction);
            Json(update, "document", session.Application);
            update.Parameters.AddWithValue("revision", session.Application.Revision);
            update.Parameters.AddWithValue("id", app.Id); update.Parameters.AddWithValue("team", session.Application.TeamId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<DirectoryPrincipal>> DirectoryAsync(string realm, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var command = new NpgsqlCommand("SELECT document::text FROM directory WHERE realm_key=@realm ORDER BY name, id", connection);
        command.Parameters.AddWithValue("realm", realm);
        return await DocumentsAsync<DirectoryPrincipal>(command, ct);
    }

    public async Task ReplaceDirectoryAsync(string realm, IReadOnlyList<DirectoryPrincipal> principals, CancellationToken ct)
    {
        if (principals.Any(p => p.RealmKey != realm) || principals.Select(p => p.Id).Distinct().Count() != principals.Count)
            throw new DomainException("invalid_directory", "The directory snapshot contains invalid identities.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@realm, 11))", connection, tx);
        gate.Parameters.AddWithValue("realm", realm); await gate.ExecuteNonQueryAsync(ct);
        var query = new NpgsqlCommand("SELECT document::text FROM directory WHERE realm_key=@realm", connection, tx);
        query.Parameters.AddWithValue("realm", realm);
        var old = await DocumentsAsync<DirectoryPrincipal>(query, ct);
        var currentIds = principals.Select(p => p.Id).ToHashSet();
        // Absence from a filtered/federated listing is NOT proof of deletion. Keep its old freshness timestamp.
        var combined = principals.Concat(old.Where(p => !currentIds.Contains(p.Id)));
        foreach (var principal in combined)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO directory(id, realm_key, name, document) VALUES (@id, @realm, @name, @document)
                ON CONFLICT(id) DO UPDATE SET name=@name, document=@document WHERE directory.realm_key=@realm
                """, connection, tx);
            command.Parameters.AddWithValue("id", principal.Id); command.Parameters.AddWithValue("realm", realm);
            command.Parameters.AddWithValue("name", principal.Name); Json(command, "document", principal);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new DomainException("identity_collision", "Directory identity belongs to another realm.", 409);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Purge only sufficiently old, unreferenced directory search snapshots.
    /// Never delete audit records, active grants, group members with effective
    /// assigned group roles, team members or principals in open operations.
    /// Caller must have completed a successful authoritative directory sync.
    /// Disabled by default unless an organization explicitly sets retention.
    /// </summary>
    public async Task<int> PruneUnreferencedDirectoryAsync(
        string realm, DateTimeOffset olderThan, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(realm) || limit is < 1 or > 1000
            || olderThan > DateTimeOffset.UtcNow.AddDays(-30))
            throw new ArgumentException("A realm, bounded batch and minimum 30-day retention are required.");

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The same per-realm lock is taken by ReplaceDirectoryAsync.
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@realm, 11))", connection, transaction))
        {
            gate.Parameters.AddWithValue("realm", realm);
            await gate.ExecuteNonQueryAsync(ct);
        }
        await using var command = new NpgsqlCommand("""
            WITH candidates AS (
                SELECT d.id FROM directory d
                WHERE d.realm_key=@realm
                  AND (d.document->>'syncedAt')::timestamptz < @cutoff
                  AND NOT EXISTS (
                    SELECT 1 FROM teams t
                    CROSS JOIN LATERAL jsonb_array_elements(t.document->'members') m
                    WHERE m->>'subjectKey' = d.document->>'subjectKey')
                  AND NOT EXISTS (
                    SELECT 1 FROM applications a
                    CROSS JOIN LATERAL jsonb_array_elements(a.document->'assignments') assigned
                    WHERE a.realm_key=d.realm_key
                      AND assigned->>'principalId'=d.id::text)
                  AND NOT EXISTS (
                    SELECT 1 FROM operations o
                    JOIN applications a ON a.id=o.application_id
                    WHERE a.realm_key=d.realm_key
                      AND o.status IN ('AwaitingApproval', 'Queued', 'Applying', 'Conflict', 'Failed')
                      AND (o.document->'change'->>'principalId'=d.id::text
                           OR o.actor_key=d.document->>'subjectKey'
                           OR o.document->'approvedBy'->>'key'=d.document->>'subjectKey'))
                  AND NOT EXISTS (
                    SELECT 1 FROM directory grp
                    JOIN applications a ON a.realm_key=d.realm_key
                    WHERE grp.realm_key=d.realm_key AND grp.document->>'kind'='Group'
                      AND COALESCE(grp.document->'effectiveMemberKeys','[]'::jsonb)
                          ? (d.document->>'subjectKey')
                      AND EXISTS (
                        SELECT 1 FROM jsonb_array_elements(a.document->'assignments') ga
                        WHERE ga->>'principalId'=grp.id::text))
                ORDER BY (d.document->>'syncedAt')::timestamptz, d.id
                LIMIT @limit FOR UPDATE OF d
            )
            DELETE FROM directory d USING candidates c WHERE d.id=c.id
            """, connection, transaction);
        command.Parameters.AddWithValue("realm", realm);
        command.Parameters.AddWithValue("cutoff", olderThan);
        command.Parameters.AddWithValue("limit", limit);
        var deleted = await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }

    public async Task<IReadOnlyList<Guid>> DueApplicationsAsync(DateTimeOffset now, int limit, Guid afterId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT id FROM applications a WHERE
              EXISTS (SELECT 1 FROM operations o WHERE o.application_id=a.id AND o.status='Queued' AND o.next_at<=@now)
              OR EXISTS (SELECT 1 FROM access_reviews r WHERE r.application_id=a.id AND NOT r.closed AND r.due_at<=@now)
              OR EXISTS (SELECT 1 FROM review_schedules s WHERE s.application_id=a.id AND s.enabled AND s.next_start_at<=@now)
              OR EXISTS (SELECT 1 FROM jsonb_array_elements(a.document->'assignments') item
                WHERE (item->>'expiresAt')::timestamptz<=@now AND NOT EXISTS (
                  SELECT 1 FROM operations o WHERE o.application_id=a.id AND o.actor_key='system'
                  AND o.status<>'Rejected' AND o.idempotency_key='revoke:'||(item->>'id')))
            -- Rotate across eligible IDs instead of repeatedly truncating to the
            -- same first 100 hot applications. When the cursor reaches the end,
            -- wrap to the beginning; a subsequent worker tick advances the cursor.
            ORDER BY (a.id <= @after), a.id LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("now", now); command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        command.Parameters.AddWithValue("after", afterId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<Guid>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0));
        return result;
    }

    public async Task<IReadOnlyList<AuditEvent>> AuditAsync(Guid appId, long after, int limit, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var command = new NpgsqlCommand("SELECT document::text FROM audit_events WHERE application_id=@id AND sequence>@after ORDER BY sequence LIMIT @limit", connection);
        command.Parameters.AddWithValue("id", appId); command.Parameters.AddWithValue("after", after);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        return await DocumentsAsync<AuditEvent>(command, ct);
    }
    public async Task<IReadOnlyList<Operation>> OperationsAsync(Guid appId, int limit, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var command = new NpgsqlCommand("SELECT document::text FROM operations WHERE application_id=@id ORDER BY created_at DESC, id LIMIT @limit", connection);
        command.Parameters.AddWithValue("id", appId); command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        return await DocumentsAsync<Operation>(command, ct);
    }
    public async Task<IReadOnlyList<AccessReview>> ReviewsAsync(Guid appId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var command = new NpgsqlCommand("SELECT document::text FROM access_reviews WHERE application_id=@id ORDER BY due_at DESC LIMIT 200", connection);
        command.Parameters.AddWithValue("id", appId);
        return await DocumentsAsync<AccessReview>(command, ct);
    }
    public async Task<AccessReviewSchedule?> ReviewScheduleAsync(Guid appId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT document::text FROM review_schedules WHERE application_id=@app", connection);
        command.Parameters.AddWithValue("app", appId);
        return await OneAsync<AccessReviewSchedule>(command, ct);
    }

    internal static void Json<T>(NpgsqlCommand command, string name, T value) => command.Parameters.AddWithValue(name, NpgsqlDbType.Jsonb, Wire.Serialize(value));
    internal static async Task<T?> OneAsync<T>(NpgsqlCommand command, CancellationToken ct) where T : class
    {
        var text = await command.ExecuteScalarAsync(ct) as string;
        return text is null ? null : Wire.Deserialize<T>(text);
    }
    private static async Task<IReadOnlyList<T>> DocumentsAsync<T>(NpgsqlCommand command, CancellationToken ct)
    {
        await using (command)
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            var result = new List<T>();
            while (await reader.ReadAsync(ct)) result.Add(Wire.Deserialize<T>(reader.GetString(0)));
            return result;
        }
    }
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync(ct);
    }
    internal static async Task AppendAuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid appId,
        Actor actor, string action, string detail, DateTimeOffset now, CancellationToken ct)
    {
        await using var previousCommand = new NpgsqlCommand("SELECT document::text FROM audit_events WHERE application_id=@id ORDER BY sequence DESC LIMIT 1", connection, tx);
        previousCommand.Parameters.AddWithValue("id", appId);
        var previous = await OneAsync<AuditEvent>(previousCommand, ct);
        var audit = AuditChain.Create(appId, actor.Key, action, detail, now, previous);
        await using var command = new NpgsqlCommand("INSERT INTO audit_events(id, application_id, sequence, document) VALUES (@id, @app, @sequence, @document)", connection, tx);
        command.Parameters.AddWithValue("id", audit.Id); command.Parameters.AddWithValue("app", appId);
        command.Parameters.AddWithValue("sequence", audit.Sequence); Json(command, "document", audit);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction, ApplicationDocument app, Team team) : IApplicationTransaction
    {
        private readonly Guid applicationId = app.Id;
        public ApplicationDocument Application { get; set; } = app;
        public Team Team { get; } = team;
        public async Task<Operation?> OperationAsync(Guid id, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("SELECT document::text FROM operations WHERE id=@id AND application_id=@app", connection, transaction);
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("app", applicationId);
            return await OneAsync<Operation>(command, ct);
        }
        public async Task<Operation?> IdempotentAsync(string actorKey, string idempotencyKey, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("SELECT document::text FROM operations WHERE application_id=@app AND actor_key=@actor AND idempotency_key=@key", connection, transaction);
            command.Parameters.AddWithValue("app", applicationId); command.Parameters.AddWithValue("actor", actorKey); command.Parameters.AddWithValue("key", idempotencyKey);
            return await OneAsync<Operation>(command, ct);
        }
        public async Task<IReadOnlyList<Operation>> UnresolvedMutationsAsync(CancellationToken ct)
        {
            var command = new NpgsqlCommand("SELECT document::text FROM operations WHERE application_id=@app AND status IN ('Failed','Conflict') AND (document->>'attempts')::integer>0 AND document->'change'->>'kind'<>'Reconcile' ORDER BY created_at", connection, transaction);
            command.Parameters.AddWithValue("app", applicationId);
            return await DocumentsAsync<Operation>(command, ct);
        }
        public async Task<Operation?> NextOperationAsync(DateTimeOffset now, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("""
                SELECT o.document::text FROM operations o

                WHERE o.application_id=@app AND o.status='Queued' AND o.next_at<=@now
                  AND (
                    -- An explicit emergency revoke may bypass earlier queued grants,
                    -- including grants in network backoff. System revocations stay first.
                    o.document->'change'->>'kind'='RevokeAccess'
                    OR (
                        -- Ordinary writes are blocked while *any* revocation is
                        -- unresolved, even when that revoke is itself in backoff.
                        NOT EXISTS (
                            SELECT 1 FROM operations revoke
                            WHERE revoke.application_id=o.application_id
                              AND revoke.status='Queued'
                              AND revoke.document->'change'->>'kind'='RevokeAccess')
                        AND NOT EXISTS (
                            SELECT 1 FROM operations prior
                            WHERE prior.application_id=o.application_id AND prior.status='Queued'
                              AND NOT COALESCE((prior.document->>'systemRevocation')::boolean, false)
                              AND (prior.created_at, prior.id) < (o.created_at, o.id))
                    )
                  )
                ORDER BY
                    CASE WHEN COALESCE((o.document->>'systemRevocation')::boolean, false) THEN 0
                         WHEN o.document->'change'->>'kind'='RevokeAccess' THEN 1
                         ELSE 2 END,
                    o.created_at, o.id LIMIT 1 FOR UPDATE OF o
                """, connection, transaction);
            command.Parameters.AddWithValue("app", applicationId);
            command.Parameters.AddWithValue("now", now);
            return await OneAsync<Operation>(command, ct);
        }
        public async Task SaveOperationAsync(Operation operation, CancellationToken ct)
        {
            if (operation.ApplicationId != applicationId) throw new InvalidOperationException("Cross-application operation.");
            await using var command = new NpgsqlCommand("""
                INSERT INTO operations(id, application_id, actor_key, idempotency_key, status, created_at, next_at, document)
                VALUES (@id, @app, @actor, @key, @status, @created, @next, @document)
                ON CONFLICT(id) DO UPDATE SET status=@status, next_at=@next, document=@document
                WHERE operations.application_id=@app
                """, connection, transaction);
            command.Parameters.AddWithValue("id", operation.Id); command.Parameters.AddWithValue("app", applicationId);
            command.Parameters.AddWithValue("actor", operation.RequestedBy.Key); command.Parameters.AddWithValue("key", operation.IdempotencyKey);
            command.Parameters.AddWithValue("status", operation.Status.ToString()); command.Parameters.AddWithValue("created", operation.CreatedAt);
            command.Parameters.AddWithValue("next", operation.NextAttemptAt); Json(command, "document", operation);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Operation identity conflict.");
        }
        public Task AppendAuditAsync(Actor actor, string action, string detail, DateTimeOffset now, CancellationToken ct) =>
            PostgresStore.AppendAuditAsync(connection, transaction, applicationId, actor, action, detail, now, ct);
        public async Task<bool> HasUnresolvedRevocationAsync(Guid assignmentId, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM operations WHERE application_id=@app AND status<>'Rejected'
                    AND document->'change'->>'kind'='RevokeAccess'
                    AND document->'change'->>'targetId'=@assignment)
                """, connection, transaction);
            command.Parameters.AddWithValue("app", applicationId);
            command.Parameters.AddWithValue("assignment", assignmentId.ToString());
            return (bool)(await command.ExecuteScalarAsync(ct))!;
        }
        public async Task<AccessReviewSchedule?> ReviewScheduleAsync(CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("SELECT document::text FROM review_schedules WHERE application_id=@app", connection, transaction);
            command.Parameters.AddWithValue("app", applicationId);
            return await OneAsync<AccessReviewSchedule>(command, ct);
        }
        public async Task SaveReviewScheduleAsync(AccessReviewSchedule schedule, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO review_schedules(application_id, enabled, next_start_at, revision, document)
                VALUES (@app, @enabled, @next, @revision, @document)
                ON CONFLICT(application_id) DO UPDATE
                    SET enabled=@enabled, next_start_at=@next, revision=@revision, document=@document
                    WHERE review_schedules.revision < @revision
                """, connection, transaction);
            command.Parameters.AddWithValue("app", applicationId);
            command.Parameters.AddWithValue("enabled", schedule.Enabled);
            command.Parameters.AddWithValue("next", schedule.NextStartAt.ToUniversalTime());
            command.Parameters.AddWithValue("revision", schedule.Revision);
            Json(command, "document", schedule);
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Review schedule revision must increase.");
        }
        public async Task<IReadOnlyList<AccessReview>> ReviewsAsync(CancellationToken ct)
        {
            var command = new NpgsqlCommand("SELECT document::text FROM access_reviews WHERE application_id=@app AND NOT closed ORDER BY due_at", connection, transaction);
            command.Parameters.AddWithValue("app", applicationId);
            return await DocumentsAsync<AccessReview>(command, ct);
        }
        public async Task SaveReviewAsync(AccessReview review, CancellationToken ct)
        {
            if (review.ApplicationId != applicationId) throw new InvalidOperationException("Cross-application review.");
            await using var command = new NpgsqlCommand("""
                INSERT INTO access_reviews(id, application_id, closed, due_at, document) VALUES (@id, @app, @closed, @due, @document)
                ON CONFLICT(id) DO UPDATE SET closed=@closed, document=@document WHERE access_reviews.application_id=@app
                """, connection, transaction);
            command.Parameters.AddWithValue("id", review.Id); command.Parameters.AddWithValue("app", applicationId);
            command.Parameters.AddWithValue("closed", review.Closed); command.Parameters.AddWithValue("due", review.DueAt);
            Json(command, "document", review);
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Review identity conflict.");
        }
    }
}
