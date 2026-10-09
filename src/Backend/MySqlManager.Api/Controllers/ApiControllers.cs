using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Contracts;
using MySqlManager.Application.Features.Auth;
using MySqlManager.Application.Features.Backups;
using MySqlManager.Application.Features.Copies;
using MySqlManager.Application.Features.Databases;
using MySqlManager.Application.Features.Git;
using MySqlManager.Application.Features.Operations;
using MySqlManager.Application.Features.Restores;
using MySqlManager.Application.Features.Servers;
using MySqlManager.Domain.Enums;

namespace MySqlManager.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IMediator _mediator;
    public AuthController(IMediator mediator) => _mediator = mediator;

    /// <summary>Issues an access token and a rotating refresh token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginBody body, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new LoginCommand(body.Email, body.Password, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"), cancellationToken);
        return Ok(ApiResponse<AuthResultDto>.Ok("LOGIN_SUCCEEDED", "Signed in.", result));
    }

    /// <summary>Rotates a refresh token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(RefreshBody body, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new RefreshTokenCommand(body.RefreshToken, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"), cancellationToken);
        return Ok(ApiResponse<AuthResultDto>.Ok("TOKEN_REFRESHED", "Token refreshed.", result));
    }

    /// <summary>Revokes a refresh token.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(RefreshBody body, CancellationToken cancellationToken)
    {
        await _mediator.Send(new LogoutCommand(body.RefreshToken), cancellationToken);
        return Ok(ApiResponse<bool>.Ok("LOGOUT_SUCCEEDED", "Signed out.", true));
    }

    /// <summary>Returns the current user profile.</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken cancellationToken) =>
        Ok(ApiResponse<UserProfileDto>.Ok("CURRENT_USER", "Current user.", await _mediator.Send(new CurrentUserQuery(), cancellationToken)));
}

public sealed record LoginBody(string Email, string Password);
public sealed record RefreshBody(string RefreshToken);

[ApiController]
[Route("api/servers")]
[Authorize]
public sealed class ServersController : ControllerBase
{
    private readonly IMediator _mediator;
    public ServersController(IMediator mediator) => _mediator = mediator;

    /// <summary>Registers a MySQL server. Passwords are encrypted and never returned.</summary>
    [HttpPost]
    [Authorize(Policy = Permissions.ServerCreate)]
    public async Task<IActionResult> Create(CreateServerBody body, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new CreateServerCommand(body.Name, body.Description, body.Host, body.Port, body.Environment, body.SslEnabled, body.SslCertificatePem, body.ConnectionTimeoutSeconds, body.IsActive, body.TimeZoneId, body.Credentials), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<ServerDto>.Ok("SERVER_CREATED", "Server created successfully.", result));
    }

    /// <summary>Lists registered servers.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.ServerView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<ServerDto>>.Ok("SERVERS_LISTED", "Servers listed.", await _mediator.Send(new GetServersQuery(), cancellationToken)));

    /// <summary>Gets one server. Credentials omit secrets.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.ServerView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<ServerDto>.Ok("SERVER_FETCHED", "Server fetched.", await _mediator.Send(new GetServerQuery(id), cancellationToken)));

    /// <summary>Updates server metadata. This does not change the customer database.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ServerEdit)]
    public async Task<IActionResult> Update(Guid id, UpdateServerBody body, CancellationToken cancellationToken) =>
        Ok(ApiResponse<ServerDto>.Ok("SERVER_UPDATED", "Server updated.", await _mediator.Send(new UpdateServerCommand(id, body.Name, body.Description, body.Host, body.Port, body.Environment, body.SslEnabled, body.SslCertificatePem, body.ConnectionTimeoutSeconds, body.IsActive, body.TimeZoneId), cancellationToken)));

    /// <summary>Removes the server registration only. No customer database is dropped.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ServerDelete)]
    public async Task<IActionResult> Delete(Guid id, ConfirmationBody body, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteServerCommand(id, body.Confirmation), cancellationToken);
        return Ok(ApiResponse<bool>.Ok("SERVER_DELETED", "Server registration removed. No database was dropped and stored backup files were kept.", true));
    }

    /// <summary>Tests connectivity with a read-only or destination credential.</summary>
    [HttpPost("{id:guid}/test-connection")]
    [Authorize(Policy = Permissions.ServerView)]
    public async Task<IActionResult> Test(Guid id, [FromQuery] CredentialPurpose purpose = CredentialPurpose.BackupReadOnly, CancellationToken cancellationToken = default) =>
        Ok(ApiResponse<ConnectionTestDto>.Ok("CONNECTION_TESTED", "Connection test finished.", await _mediator.Send(new TestConnectionCommand(id, purpose), cancellationToken)));

    /// <summary>Discovers databases with read-only metadata queries.</summary>
    [HttpGet("{id:guid}/databases")]
    [Authorize(Policy = Permissions.ServerView)]
    public async Task<IActionResult> Databases(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<DatabaseDto>>.Ok("DATABASES_DISCOVERED", "Databases discovered.", await _mediator.Send(new DiscoverDatabasesCommand(id), cancellationToken)));

    /// <summary>Returns the latest health snapshot. Host CPU and memory stay empty until an agent reports them.</summary>
    [HttpGet("{id:guid}/health")]
    [Authorize(Policy = Permissions.ServerView)]
    public async Task<IActionResult> Health(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<HealthDto>.Ok("SERVER_HEALTH", "Server health.", await _mediator.Send(new GetServerHealthQuery(id), cancellationToken)));

    /// <summary>Stores an encrypted credential. Source credentials must be read-only.</summary>
    [HttpPut("{id:guid}/credentials")]
    [Authorize(Policy = Permissions.ServerEdit)]
    public async Task<IActionResult> Credential(Guid id, CredentialInput body, CancellationToken cancellationToken) =>
        Ok(ApiResponse<CredentialDto>.Ok("CREDENTIAL_SAVED", "Credential saved.", await _mediator.Send(new UpsertCredentialCommand(id, body), cancellationToken)));
}

public sealed record CreateServerBody(string Name, string? Description, string Host, int Port, ServerEnvironment Environment, bool SslEnabled, string? SslCertificatePem, int ConnectionTimeoutSeconds, bool IsActive, string? TimeZoneId, IReadOnlyList<CredentialInput> Credentials);
public sealed record UpdateServerBody(string Name, string? Description, string Host, int Port, ServerEnvironment Environment, bool SslEnabled, string? SslCertificatePem, int ConnectionTimeoutSeconds, bool IsActive, string? TimeZoneId);
public sealed record ConfirmationBody(string Confirmation);

[ApiController]
[Route("api/databases")]
[Authorize]
public sealed class DatabasesController : ControllerBase
{
    private readonly IMediator _mediator;
    public DatabasesController(IMediator mediator) => _mediator = mediator;

    /// <summary>Lists discovered databases in the management catalog.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.ServerView)]
    public async Task<IActionResult> List([FromQuery] Guid? serverId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<DatabaseDto>>.Ok("DATABASES_LISTED", "Databases listed.", await _mediator.Send(new ListDatabasesQuery(serverId), cancellationToken)));

    /// <summary>Gets one catalog database. This does not modify MySQL.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.ServerView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<DatabaseDto>.Ok("DATABASE_FETCHED", "Database fetched.", await _mediator.Send(new GetDatabaseQuery(id), cancellationToken)));

    /// <summary>Selects databases for backup in the management catalog.</summary>
    [HttpPost("selection")]
    [Authorize(Policy = Permissions.BackupCreate)]
    public async Task<IActionResult> Select(SelectDatabasesBody body, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<DatabaseDto>>.Ok("DATABASES_SELECTED", "Backup selection updated.", await _mediator.Send(new SelectDatabasesCommand(body.ServerId, body.DatabaseIds), cancellationToken)));

    /// <summary>Deactivates a catalog row. The MySQL database is not dropped.</summary>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = Permissions.ServerEdit)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeactivateDatabaseCommand(id), cancellationToken);
        return Ok(ApiResponse<bool>.Ok("DATABASE_DEACTIVATED", "Catalog entry deactivated. The source database was not modified.", true));
    }
}

public sealed record SelectDatabasesBody(Guid ServerId, IReadOnlyList<Guid> DatabaseIds);

[ApiController]
[Route("api/backup-configurations")]
[Authorize]
public sealed class BackupConfigurationsController : ControllerBase
{
    private readonly IMediator _mediator;
    public BackupConfigurationsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Creates or updates a backup configuration and its schedule.</summary>
    [HttpPost]
    [Authorize(Policy = Permissions.BackupCreate)]
    public async Task<IActionResult> Upsert(UpsertBackupConfigurationCommand command, CancellationToken cancellationToken) =>
        Ok(ApiResponse<BackupConfigurationDto>.Ok("BACKUP_CONFIGURATION_SAVED", "Backup configuration saved.", await _mediator.Send(command, cancellationToken)));

    /// <summary>Lists backup configurations.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.BackupView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<BackupConfigurationDto>>.Ok("BACKUP_CONFIGURATIONS_LISTED", "Backup configurations listed.", await _mediator.Send(new ListBackupConfigurationsQuery(), cancellationToken)));
}

[ApiController]
[Route("api/backups")]
[Authorize]
public sealed class BackupsController : ControllerBase
{
    private readonly IMediator _mediator;
    public BackupsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Queues a full backup and returns immediately.</summary>
    [HttpPost]
    [Authorize(Policy = Permissions.BackupCreate)]
    public async Task<IActionResult> Create(StartBackupBody body, CancellationToken cancellationToken)
    {
        var allDatabases = body.DatabaseId is null || body.DatabaseId == Guid.Empty;
        var result = await _mediator.Send(new StartBackupCommand(body.ServerId, allDatabases ? null : body.DatabaseId), cancellationToken);
        var message = allDatabases
            ? "Backup jobs created for every discovered database. Each database keeps its own retention set."
            : "Backup job created.";
        return StatusCode(StatusCodes.Status202Accepted, ApiResponse<JobCreatedDto>.Ok("BACKUP_JOB_CREATED", message, result));
    }

    /// <summary>Lists backup artifacts.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.BackupView)]
    public async Task<IActionResult> List([FromQuery] Guid? serverId, [FromQuery] Guid? databaseId, [FromQuery] string? status, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<BackupFileDto>>.Ok("BACKUPS_LISTED", "Backups listed.", await _mediator.Send(new ListBackupsQuery(serverId, databaseId, status, fromUtc, toUtc), cancellationToken)));

    /// <summary>Gets one backup artifact.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.BackupView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<BackupFileDto>.Ok("BACKUP_FETCHED", "Backup fetched.", await _mediator.Send(new GetBackupQuery(id), cancellationToken)));

    /// <summary>Deletes a backup file from application storage only.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.BackupDelete)]
    public async Task<IActionResult> Delete(Guid id, ConfirmationBody body, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteBackupCommand(id, body.Confirmation), cancellationToken);
        return Ok(ApiResponse<bool>.Ok("BACKUP_DELETED", "Backup artifact deleted from application storage.", true));
    }
}

public sealed record StartBackupBody(Guid ServerId, Guid? DatabaseId);

[ApiController]
[Route("api/migrations")]
[Authorize]
public sealed class MigrationsController : ControllerBase
{
    private readonly IMediator _mediator;
    public MigrationsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Queues one copy job per selected database. Source databases are never modified, and an existing destination is never dropped.</summary>
    [HttpPost("batch")]
    [Authorize(Policy = Permissions.MigrationCreate)]
    public async Task<IActionResult> CreateBatch(StartCopyBatchCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);
        var skipped = result.Skipped.Count == 0
            ? string.Empty
            : " Skipped: " + string.Join("; ", result.Skipped.Select(item => $"{item.DatabaseName}: {item.Reason}"));
        var message = result.QueuedCount == 0
            ? "No databases were queued." + skipped
            : $"{result.QueuedCount} copy jobs queued. Source databases remain unchanged." + skipped;
        return StatusCode(StatusCodes.Status202Accepted, ApiResponse<CopyBatchResultDto>.Ok(result.QueuedCount == 0 ? "COPY_BATCH_EMPTY" : "COPY_BATCH_CREATED", message, result));
    }

    /// <summary>Queues a copy. The source database is never modified.</summary>
    [HttpPost]
    [Authorize(Policy = Permissions.MigrationCreate)]
    public async Task<IActionResult> Create(StartCopyCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);
        return StatusCode(StatusCodes.Status202Accepted, ApiResponse<JobCreatedDto>.Ok("COPY_JOB_CREATED", "Copy job created. The source database will remain unchanged.", result));
    }

    /// <summary>Lists copy and clone jobs.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.MigrationView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<MigrationJobDto>>.Ok("COPIES_LISTED", "Copy jobs listed.", await _mediator.Send(new ListMigrationsQuery(), cancellationToken)));

    /// <summary>Gets a copy job and its report.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.MigrationView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<MigrationJobDto>.Ok("COPY_FETCHED", "Copy job fetched.", await _mediator.Send(new GetMigrationQuery(id), cancellationToken)));

    /// <summary>Downloads the copy report.</summary>
    [HttpGet("{id:guid}/report")]
    [Authorize(Policy = Permissions.MigrationView)]
    public async Task<IActionResult> Report(Guid id, CancellationToken cancellationToken)
    {
        var job = await _mediator.Send(new GetMigrationQuery(id), cancellationToken);
        return File(System.Text.Encoding.UTF8.GetBytes(job.ReportJson ?? "{}"), "application/json", $"copy-{id}.json");
    }
}

[ApiController]
[Route("api/restores")]
[Authorize]
public sealed class RestoresController : ControllerBase
{
    private readonly IMediator _mediator;
    public RestoresController(IMediator mediator) => _mediator = mediator;

    /// <summary>Queues a restore into a new destination database. Existing databases are not overwritten.</summary>
    [HttpPost]
    [Authorize(Policy = Permissions.BackupRestore)]
    public async Task<IActionResult> Create(StartRestoreCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken);
        return StatusCode(StatusCodes.Status202Accepted, ApiResponse<JobCreatedDto>.Ok("RESTORE_JOB_CREATED", result.Detail ?? "Restore job created.", result));
    }

    /// <summary>Lists restore jobs.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.BackupView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<RestoreJobDto>>.Ok("RESTORES_LISTED", "Restore jobs listed.", await _mediator.Send(new ListRestoresQuery(), cancellationToken)));

    /// <summary>Gets a restore job.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.BackupView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RestoreJobDto>.Ok("RESTORE_FETCHED", "Restore job fetched.", await _mediator.Send(new GetRestoreQuery(id), cancellationToken)));

    /// <summary>Downloads the restore report.</summary>
    [HttpGet("{id:guid}/report")]
    [Authorize(Policy = Permissions.BackupView)]
    public async Task<IActionResult> Report(Guid id, CancellationToken cancellationToken)
    {
        var job = await _mediator.Send(new GetRestoreQuery(id), cancellationToken);
        return File(System.Text.Encoding.UTF8.GetBytes(job.ReportJson ?? "{}"), "application/json", $"restore-{id}.json");
    }
}

[ApiController]
[Route("api/git-repositories")]
[Authorize]
public sealed class GitRepositoriesController : ControllerBase
{
    private readonly IMediator _mediator;
    public GitRepositoriesController(IMediator mediator) => _mediator = mediator;

    /// <summary>Saves a backup Git repository. Credentials are encrypted and are not written into the repository.</summary>
    [HttpPost]
    [Authorize(Policy = Permissions.GitConfigure)]
    public async Task<IActionResult> Upsert(UpsertGitRepositoryCommand command, CancellationToken cancellationToken) =>
        Ok(ApiResponse<GitRepositoryDto>.Ok("GIT_REPOSITORY_SAVED", "Git repository saved.", await _mediator.Send(command, cancellationToken)));

    /// <summary>Lists configured backup repositories.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.GitView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<GitRepositoryDto>>.Ok("GIT_REPOSITORIES_LISTED", "Git repositories listed.", await _mediator.Send(new ListGitRepositoriesQuery(), cancellationToken)));

    /// <summary>Tests Git authentication without pushing backup contents.</summary>
    [HttpPost("{id:guid}/test")]
    [Authorize(Policy = Permissions.GitConfigure)]
    public async Task<IActionResult> Test(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<GitConnectionReport>.Ok("GIT_CONNECTION_TESTED", "Git connection tested.", await _mediator.Send(new TestGitConnectionCommand(id), cancellationToken)));

    /// <summary>Shows recent Git commits for the backup repository.</summary>
    [HttpGet("{id:guid}/history")]
    [Authorize(Policy = Permissions.GitView)]
    public async Task<IActionResult> History(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<GitLogDto>>.Ok("GIT_HISTORY", "Git history.", await _mediator.Send(new GetGitLogQuery(id), cancellationToken)));

    /// <summary>Rewrites Git history only when AllowGitHistoryRewrite is enabled. Older blobs become unrecoverable.</summary>
    [HttpPost("{id:guid}/purge")]
    [Authorize(Policy = Permissions.GitPurge)]
    public async Task<IActionResult> Purge(Guid id, ConfirmationBody body, CancellationToken cancellationToken)
    {
        await _mediator.Send(new PurgeGitHistoryCommand(id, body.Confirmation), cancellationToken);
        return Ok(ApiResponse<bool>.Ok("GIT_HISTORY_PURGED", "Older Git backup versions are no longer recoverable from this repository.", true));
    }
}

[ApiController]
[Route("api/jobs")]
[Authorize]
public sealed class JobsController : ControllerBase
{
    private readonly IMediator _mediator;
    public JobsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Lists backup, copy, clone, and restore jobs.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.BackupView)]
    public async Task<IActionResult> List([FromQuery] Guid? serverId, [FromQuery] string? jobType, [FromQuery] string? status, [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<JobListItemDto>>.Ok("JOBS_LISTED", "Jobs listed.", await _mediator.Send(new ListJobsQuery(serverId, jobType, status, fromUtc, toUtc), cancellationToken)));

    /// <summary>Returns logs for a job.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.BackupView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<JobLogDto>>.Ok("JOB_LOGS", "Job logs.", await _mediator.Send(new GetJobLogsQuery(id), cancellationToken)));

    /// <summary>Requests cancellation. A cancelled job is not marked successful.</summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Permissions.MigrationCancel)]
    public async Task<IActionResult> Cancel(Guid id, [FromQuery] string jobType, CancellationToken cancellationToken)
    {
        await _mediator.Send(new CancelJobCommand(id, jobType), cancellationToken);
        return Ok(ApiResponse<bool>.Ok("JOB_CANCEL_REQUESTED", "Cancellation requested.", true));
    }
}

[ApiController]
[Route("api/dashboard")]
[Authorize]
public sealed class DashboardController : ControllerBase
{
    private readonly IMediator _mediator;
    public DashboardController(IMediator mediator) => _mediator = mediator;

    /// <summary>Returns dashboard aggregates.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.ServerView)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(ApiResponse<DashboardDto>.Ok("DASHBOARD", "Dashboard.", await _mediator.Send(new GetDashboardQuery(), cancellationToken)));

    /// <summary>Returns the backup calendar for a month.</summary>
    [HttpGet("calendar")]
    [Authorize(Policy = Permissions.BackupView)]
    public async Task<IActionResult> Calendar([FromQuery] int year, [FromQuery] int month, [FromQuery] Guid? serverId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<CalendarServerDto>>.Ok("BACKUP_CALENDAR", "Backup calendar.", await _mediator.Send(new GetCalendarQuery(year, month, serverId), cancellationToken)));
}

[ApiController]
[Route("api/audit-logs")]
[Authorize(Policy = Permissions.AuditView)]
public sealed class AuditLogsController : ControllerBase
{
    private readonly IMediator _mediator;
    public AuditLogsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Lists audit events. Secrets are not stored in the details.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        Ok(ApiResponse<PagedResult<AuditLogDto>>.Ok("AUDIT_LOGS", "Audit logs.", await _mediator.Send(new ListAuditQuery(page, pageSize), cancellationToken)));
}

[ApiController]
[Route("api/users")]
[Authorize(Policy = Permissions.UserManage)]
public sealed class UsersController : ControllerBase
{
    private readonly IMediator _mediator;
    public UsersController(IMediator mediator) => _mediator = mediator;

    /// <summary>Lists users.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<UserProfileDto>>.Ok("USERS_LISTED", "Users listed.", await _mediator.Send(new ListUsersQuery(), cancellationToken)));

    /// <summary>Creates a user.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CreateUserCommand command, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, ApiResponse<UserProfileDto>.Ok("USER_CREATED", "User created.", await _mediator.Send(command, cancellationToken)));
}

[ApiController]
[Route("api/roles")]
[Authorize(Policy = Permissions.UserManage)]
public sealed class RolesController : ControllerBase
{
    private readonly IMediator _mediator;
    public RolesController(IMediator mediator) => _mediator = mediator;

    /// <summary>Lists roles and permissions.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<RoleDto>>.Ok("ROLES_LISTED", "Roles listed.", await _mediator.Send(new ListRolesQuery(), cancellationToken)));

    /// <summary>Replaces the permissions of a role. SuperAdmin cannot be reduced.</summary>
    [HttpPut("{id:guid}/permissions")]
    public async Task<IActionResult> Update(Guid id, PermissionBody body, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RoleDto>.Ok("ROLE_UPDATED", "Role updated.", await _mediator.Send(new UpdateRolePermissionsCommand(id, body.Permissions), cancellationToken)));
}

public sealed record PermissionBody(IReadOnlyList<string> Permissions);

[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController : ControllerBase
{
    private readonly IMediator _mediator;
    public NotificationsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Lists notification deliveries.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.AuditView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<NotificationDto>>.Ok("NOTIFICATIONS", "Notifications.", await _mediator.Send(new ListNotificationsQuery(), cancellationToken)));

    /// <summary>Lists notification endpoints.</summary>
    [HttpGet("endpoints")]
    [Authorize(Policy = Permissions.UserManage)]
    public async Task<IActionResult> Endpoints(CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<NotificationEndpointDto>>.Ok("NOTIFICATION_ENDPOINTS", "Notification endpoints.", await _mediator.Send(new ListNotificationEndpointsQuery(), cancellationToken)));

    /// <summary>Saves an email or webhook endpoint.</summary>
    [HttpPost("endpoints")]
    [Authorize(Policy = Permissions.UserManage)]
    public async Task<IActionResult> Save(UpsertNotificationEndpointCommand command, CancellationToken cancellationToken) =>
        Ok(ApiResponse<NotificationEndpointDto>.Ok("NOTIFICATION_ENDPOINT_SAVED", "Notification endpoint saved.", await _mediator.Send(command, cancellationToken)));
}

[ApiController]
[Route("api/settings")]
[Authorize]
public sealed class SettingsController : ControllerBase
{
    private readonly IMediator _mediator;
    public SettingsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Returns non-secret runtime settings.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.ServerView)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(ApiResponse<SettingsDto>.Ok("SETTINGS", "Settings.", await _mediator.Send(new GetSettingsQuery(), cancellationToken)));

    /// <summary>Rotates the active backup data key. Old keys remain available for restore.</summary>
    [HttpPost("encryption-keys/rotate")]
    [Authorize(Policy = Permissions.UserManage)]
    public async Task<IActionResult> Rotate(CancellationToken cancellationToken) =>
        Ok(ApiResponse<string>.Ok("ENCRYPTION_KEY_ROTATED", "Encryption key rotated.", await _mediator.Send(new RotateEncryptionKeyCommand(), cancellationToken)));
}
