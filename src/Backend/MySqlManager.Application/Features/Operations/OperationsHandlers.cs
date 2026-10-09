using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Contracts;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Identity;

namespace MySqlManager.Application.Features.Operations;

public sealed record GetDashboardQuery : IRequest<DashboardDto>;
public sealed class GetDashboardHandler : IRequestHandler<GetDashboardQuery, DashboardDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IBackupStorage _storage;
    private readonly IClock _clock;

    public GetDashboardHandler(IApplicationDbContext db, IBackupStorage storage, IClock clock)
    {
        _db = db;
        _storage = storage;
        _clock = clock;
    }

    public async Task<DashboardDto> Handle(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        var today = _clock.UtcNow.Date;
        var servers = await _db.Servers.AsNoTracking().Where(s => !s.IsDeleted).Select(s => s.Id).ToListAsync(cancellationToken);
        var healthRows = await _db.ServerHealthSnapshots.AsNoTracking().Where(s => servers.Contains(s.ServerId)).ToListAsync(cancellationToken);
        var latestHealth = healthRows.GroupBy(s => s.ServerId).Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First()).ToList();
        var online = latestHealth.Count(h => h.IsOnline);
        var databases = await _db.Databases.CountAsync(d => !d.IsDeleted && d.IsActive, cancellationToken);
        var success = await _db.BackupJobs.CountAsync(j => j.Status == BackupJobStatus.Completed && j.CompletedAtUtc >= today, cancellationToken);
        var failed = await _db.BackupJobs.CountAsync(j => j.Status == BackupJobStatus.Failed && j.CompletedAtUtc >= today, cancellationToken);
        var runningBackups = await _db.BackupJobs.CountAsync(j => j.Status == BackupJobStatus.Running || j.Status == BackupJobStatus.Pending, cancellationToken);
        var runningCopies = await _db.MigrationJobs.CountAsync(j => j.Status < PipelineJobStatus.Completed, cancellationToken);
        var disk = _storage.GetDiskUsage();
        var used = await _db.BackupFiles.Where(f => f.Status == BackupFileStatus.Completed && !f.IsDeleted).SumAsync(f => (long?)f.CompressedSizeBytes, cancellationToken) ?? 0;
        var audits = await _db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Timestamp).Take(12).ToListAsync(cancellationToken);
        var recentJobs = await _db.BackupJobs.AsNoTracking().Where(j => j.CompletedAtUtc >= today.AddDays(-13)).ToListAsync(cancellationToken);
        var outcomes = recentJobs.GroupBy(j => j.CompletedAtUtc!.Value.Date).Select(g => new ChartPointDto(g.Key.ToString("yyyy-MM-dd"), g.Count(x => x.Status == BackupJobStatus.Completed))).ToList();
        var sizes = await _db.Databases.AsNoTracking().Where(d => !d.IsDeleted).OrderByDescending(d => d.SizeBytes).Take(8)
            .Select(d => new ChartPointDto(d.Name, d.SizeBytes)).ToListAsync(cancellationToken);
        var migrationRows = await _db.MigrationJobs.AsNoTracking().Where(j => j.CreatedAtUtc >= today.AddDays(-13)).ToListAsync(cancellationToken);
        var migrations = migrationRows.GroupBy(j => j.CreatedAtUtc.Date).Select(g => new ChartPointDto(g.Key.ToString("yyyy-MM-dd"), g.Count())).ToList();
        var fileRows = await _db.BackupFiles.AsNoTracking().Where(f => f.Status == BackupFileStatus.Completed && f.CreatedAtUtc >= today.AddDays(-13)).ToListAsync(cancellationToken);
        var growth = fileRows.GroupBy(f => f.CreatedAtUtc.Date).Select(g => new ChartPointDto(g.Key.ToString("yyyy-MM-dd"), g.Sum(x => x.CompressedSizeBytes))).ToList();
        return new DashboardDto(servers.Count, online, Math.Max(0, servers.Count - online), databases, success, failed, runningBackups, runningCopies, used, disk.FreeBytes,
            audits.Select(a => new ActivityDto(a.Timestamp, a.Action, a.Details)).ToList(), outcomes, growth, sizes, migrations);
    }
}

public sealed record GetCalendarQuery(int Year, int Month, Guid? ServerId) : IRequest<IReadOnlyList<CalendarServerDto>>;
public sealed class GetCalendarHandler : IRequestHandler<GetCalendarQuery, IReadOnlyList<CalendarServerDto>>
{
    private readonly IApplicationDbContext _db;
    public GetCalendarHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<CalendarServerDto>> Handle(GetCalendarQuery request, CancellationToken cancellationToken)
    {
        var start = new DateTime(request.Year, request.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var servers = _db.Servers.AsNoTracking().Where(s => !s.IsDeleted);
        if (request.ServerId is not null) servers = servers.Where(s => s.Id == request.ServerId);
        var serverRows = await servers.OrderBy(s => s.Name).ToListAsync(cancellationToken);
        var files = await _db.BackupFiles.AsNoTracking().Where(f => !f.IsDeleted && f.CreatedAtUtc >= start && f.CreatedAtUtc < end).ToListAsync(cancellationToken);
        var days = DateTime.DaysInMonth(request.Year, request.Month);
        return serverRows.Select(server =>
        {
            var serverFiles = files.Where(f => f.ServerId == server.Id).ToList();
            var items = Enumerable.Range(1, days).Select(day =>
            {
                var date = new DateOnly(request.Year, request.Month, day);
                var match = serverFiles.Where(f => DateOnly.FromDateTime(f.CreatedAtUtc) == date).OrderByDescending(f => f.CreatedAtUtc).FirstOrDefault();
                var status = match is null ? "Missing" : match.Status == BackupFileStatus.Completed && match.VerificationStatus == VerificationStatus.Passed ? "Success" : match.Status == BackupFileStatus.Failed || match.VerificationStatus == VerificationStatus.Failed ? "Failed" : "Other";
                return new CalendarDayDto(date, status, match?.Id);
            }).ToList();
            return new CalendarServerDto(server.Id, server.Name, items);
        }).ToList();
    }
}

public sealed record ListJobsQuery(Guid? ServerId, string? JobType, string? Status, DateTime? FromUtc, DateTime? ToUtc) : IRequest<IReadOnlyList<JobListItemDto>>;
public sealed class ListJobsHandler : IRequestHandler<ListJobsQuery, IReadOnlyList<JobListItemDto>>
{
    private readonly IApplicationDbContext _db;
    public ListJobsHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<JobListItemDto>> Handle(ListJobsQuery request, CancellationToken cancellationToken)
    {
        var servers = await _db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var databases = await _db.Databases.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        var items = new List<JobListItemDto>();
        if (request.JobType is null or "" or "Backup")
        {
            var rows = await _db.BackupJobs.AsNoTracking().OrderByDescending(j => j.CreatedAtUtc).Take(300).ToListAsync(cancellationToken);
            items.AddRange(rows.Where(j => Match(request, j.ServerId, j.Status.ToString(), j.CreatedAtUtc)).Select(j => new JobListItemDto(j.Id, "Backup", j.Status.ToString(), servers.GetValueOrDefault(j.ServerId), databases.GetValueOrDefault(j.DatabaseId), j.CreatedAtUtc, j.CompletedAtUtc, j.ErrorMessage)));
        }
        if (request.JobType is null or "" or "Migration" or "Clone")
        {
            var rows = await _db.MigrationJobs.AsNoTracking().OrderByDescending(j => j.CreatedAtUtc).Take(300).ToListAsync(cancellationToken);
            items.AddRange(rows.Where(j => Match(request, j.SourceServerId, j.Status.ToString(), j.CreatedAtUtc)).Select(j => new JobListItemDto(j.Id, j.Mode == CopyMode.Clone ? "Clone" : "Migration", j.Status.ToString(), servers.GetValueOrDefault(j.SourceServerId), databases.GetValueOrDefault(j.SourceDatabaseId), j.CreatedAtUtc, j.CompletedAtUtc, j.ErrorMessage)));
        }
        if (request.JobType is null or "" or "Restore")
        {
            var rows = await _db.RestoreJobs.AsNoTracking().OrderByDescending(j => j.CreatedAtUtc).Take(300).ToListAsync(cancellationToken);
            items.AddRange(rows.Where(j => Match(request, j.DestinationServerId, j.Status.ToString(), j.CreatedAtUtc)).Select(j => new JobListItemDto(j.Id, "Restore", j.Status.ToString(), servers.GetValueOrDefault(j.DestinationServerId), j.DestinationDatabaseName, j.CreatedAtUtc, j.CompletedAtUtc, j.ErrorMessage)));
        }
        return items.OrderByDescending(i => i.CreatedAtUtc).Take(400).ToList();
    }

    private static bool Match(ListJobsQuery request, Guid serverId, string status, DateTime created)
    {
        if (request.ServerId is not null && request.ServerId != serverId) return false;
        if (!string.IsNullOrWhiteSpace(request.Status) && !status.Equals(request.Status, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.FromUtc is not null && created < request.FromUtc) return false;
        if (request.ToUtc is not null && created > request.ToUtc) return false;
        return true;
    }
}

public sealed record GetJobLogsQuery(Guid JobId) : IRequest<IReadOnlyList<JobLogDto>>;
public sealed class GetJobLogsHandler : IRequestHandler<GetJobLogsQuery, IReadOnlyList<JobLogDto>>
{
    private readonly IApplicationDbContext _db;
    public GetJobLogsHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<JobLogDto>> Handle(GetJobLogsQuery request, CancellationToken cancellationToken)
    {
        var rows = await _db.JobLogs.AsNoTracking().Where(l => l.JobId == request.JobId).OrderBy(l => l.CreatedAtUtc).ToListAsync(cancellationToken);
        return rows.Select(l => new JobLogDto(l.Id, l.JobId, l.JobType.ToString(), l.Level, l.Message, l.CreatedAtUtc)).ToList();
    }
}

public sealed record ListAuditQuery(int Page, int PageSize) : IRequest<PagedResult<AuditLogDto>>;
public sealed class ListAuditHandler : IRequestHandler<ListAuditQuery, PagedResult<AuditLogDto>>
{
    private readonly IApplicationDbContext _db;
    public ListAuditHandler(IApplicationDbContext db) => _db = db;
    public async Task<PagedResult<AuditLogDto>> Handle(ListAuditQuery request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 200);
        var query = _db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Timestamp);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new PagedResult<AuditLogDto>(rows.Select(a => new AuditLogDto(a.Id, a.UserId, a.Action, a.EntityType, a.EntityId, a.ServerId, a.DatabaseId, a.DestinationServerId, a.DestinationDatabase, a.Timestamp, a.IpAddress, a.Result.ToString(), a.Details, a.SourceModification)).ToList(), page, size, total);
    }
}

public sealed record ListUsersQuery : IRequest<IReadOnlyList<UserProfileDto>>;
public sealed class ListUsersHandler : IRequestHandler<ListUsersQuery, IReadOnlyList<UserProfileDto>>
{
    private readonly IApplicationDbContext _db;
    public ListUsersHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<UserProfileDto>> Handle(ListUsersQuery request, CancellationToken cancellationToken)
    {
        var users = await _db.Users.AsNoTracking().Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission).Where(u => !u.IsDeleted).ToListAsync(cancellationToken);
        return users.Select(u => new UserProfileDto(u.Id, u.Email, u.DisplayName, u.UserRoles.Select(r => r.Role.Name).ToList(), u.UserRoles.SelectMany(r => r.Role.RolePermissions).Select(p => p.Permission.Code).Distinct().ToList())).ToList();
    }
}

public sealed record CreateUserCommand(string Email, string DisplayName, string Password, string? RoleName, IReadOnlyList<string>? RoleNames) : IRequest<UserProfileDto>;
public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.DisplayName).NotEmpty();
        RuleFor(x => x.Password).MinimumLength(12);
        RuleFor(x => x).Must(command =>
                !string.IsNullOrWhiteSpace(command.RoleName) ||
                (command.RoleNames?.Any(name => !string.IsNullOrWhiteSpace(name)) ?? false))
            .WithMessage("Select at least one role.");
    }
}

public sealed class CreateUserHandler : IRequestHandler<CreateUserCommand, UserProfileDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _passwords;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;
    private readonly IAuditWriter _audit;

    public CreateUserHandler(IApplicationDbContext db, IPasswordHasher passwords, IClock clock, ICurrentUser current, IAuditWriter audit)
    {
        _db = db;
        _passwords = passwords;
        _clock = clock;
        _current = current;
        _audit = audit;
    }

    public async Task<UserProfileDto> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == email && !u.IsDeleted, cancellationToken))
        {
            throw new ConflictException("USER_EXISTS", "A user with that email already exists.");
        }
        var requested = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.RoleName))
        {
            requested.Add(request.RoleName.Trim());
        }

        if (request.RoleNames is not null)
        {
            requested.AddRange(request.RoleNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()));
        }

        var roleNames = requested.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roles = await _db.Roles.Where(r => !r.IsDeleted).ToListAsync(cancellationToken);
        var matched = roles.Where(role => roleNames.Contains(role.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (matched.Count != roleNames.Count)
        {
            throw new NotFoundException("One or more roles were not found.");
        }

        var user = new User
        {
            Email = email,
            UserName = email,
            DisplayName = request.DisplayName.Trim(),
            IsActive = true,
            CreatedAtUtc = _clock.UtcNow,
            CreatedBy = _current.UserId
        };
        user.PasswordHash = _passwords.Hash(user, request.Password);
        foreach (var role in matched)
        {
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.UserCreated, "User", user.Id.ToString(), null, null, null, null, Domain.Enums.AuditResult.Success, $"Created user {user.Email} with roles {string.Join(", ", matched.Select(role => role.Name))}."), cancellationToken);
        return new UserProfileDto(user.Id, user.Email, user.DisplayName, matched.Select(role => role.Name).ToList(), []);
    }
}

public sealed record ListRolesQuery : IRequest<IReadOnlyList<RoleDto>>;
public sealed class ListRolesHandler : IRequestHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    private readonly IApplicationDbContext _db;
    public ListRolesHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<RoleDto>> Handle(ListRolesQuery request, CancellationToken cancellationToken)
    {
        var roles = await _db.Roles.AsNoTracking().Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission).Where(r => !r.IsDeleted).ToListAsync(cancellationToken);
        return roles.Select(r => new RoleDto(r.Id, r.Name, r.Description, r.RolePermissions.Select(p => p.Permission.Code).ToList())).ToList();
    }
}

public sealed record UpdateRolePermissionsCommand(Guid RoleId, IReadOnlyList<string> Permissions) : IRequest<RoleDto>;
public sealed class UpdateRolePermissionsHandler : IRequestHandler<UpdateRolePermissionsCommand, RoleDto>
{
    private readonly IApplicationDbContext _db;
    public UpdateRolePermissionsHandler(IApplicationDbContext db) => _db = db;
    public async Task<RoleDto> Handle(UpdateRolePermissionsCommand request, CancellationToken cancellationToken)
    {
        var role = await _db.Roles.Include(r => r.RolePermissions).FirstOrDefaultAsync(r => r.Id == request.RoleId && !r.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Role was not found.");
        if (role.Name == "SuperAdmin")
        {
            throw new ConflictException("ROLE_PROTECTED", "The SuperAdmin role always retains every permission.");
        }
        var permissions = await _db.Permissions.Where(p => request.Permissions.Contains(p.Code)).ToListAsync(cancellationToken);
        _db.RolePermissions.RemoveRange(role.RolePermissions);
        foreach (var permission in permissions)
        {
            _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        }
        await _db.SaveChangesAsync(cancellationToken);
        return new RoleDto(role.Id, role.Name, role.Description, permissions.Select(p => p.Code).ToList());
    }
}

public sealed record ListNotificationsQuery : IRequest<IReadOnlyList<NotificationDto>>;
public sealed class ListNotificationsHandler : IRequestHandler<ListNotificationsQuery, IReadOnlyList<NotificationDto>>
{
    private readonly IApplicationDbContext _db;
    public ListNotificationsHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<NotificationDto>> Handle(ListNotificationsQuery request, CancellationToken cancellationToken)
    {
        var rows = await _db.Notifications.AsNoTracking().OrderByDescending(n => n.CreatedAtUtc).Take(200).ToListAsync(cancellationToken);
        return rows.Select(n => new NotificationDto(n.Id, n.Channel.ToString(), n.EventType, n.Recipient, n.Status.ToString(), n.CreatedAtUtc, n.SentAtUtc, n.Error)).ToList();
    }
}

public sealed record ListNotificationEndpointsQuery : IRequest<IReadOnlyList<NotificationEndpointDto>>;
public sealed class ListNotificationEndpointsHandler : IRequestHandler<ListNotificationEndpointsQuery, IReadOnlyList<NotificationEndpointDto>>
{
    private readonly IApplicationDbContext _db;
    public ListNotificationEndpointsHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<NotificationEndpointDto>> Handle(ListNotificationEndpointsQuery request, CancellationToken cancellationToken)
    {
        var rows = await _db.NotificationEndpoints.AsNoTracking().Where(e => !e.IsDeleted).ToListAsync(cancellationToken);
        return rows.Select(e => new NotificationEndpointDto(e.Id, e.Channel.ToString(), e.Name, e.Destination, e.IsEnabled, e.EventTypesCsv)).ToList();
    }
}

public sealed record UpsertNotificationEndpointCommand(Guid? Id, NotificationChannel Channel, string Name, string Destination, bool IsEnabled, string EventTypesCsv) : IRequest<NotificationEndpointDto>;
public sealed class UpsertNotificationEndpointValidator : AbstractValidator<UpsertNotificationEndpointCommand>
{
    public UpsertNotificationEndpointValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Destination).NotEmpty();
    }
}

public sealed class UpsertNotificationEndpointHandler : IRequestHandler<UpsertNotificationEndpointCommand, NotificationEndpointDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;
    public UpsertNotificationEndpointHandler(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }
    public async Task<NotificationEndpointDto> Handle(UpsertNotificationEndpointCommand request, CancellationToken cancellationToken)
    {
        var row = request.Id is null ? null : await _db.NotificationEndpoints.FirstOrDefaultAsync(e => e.Id == request.Id && !e.IsDeleted, cancellationToken);
        if (row is null)
        {
            row = new Domain.Operations.NotificationEndpoint { CreatedAtUtc = _clock.UtcNow };
            _db.NotificationEndpoints.Add(row);
        }
        row.Channel = request.Channel;
        row.Name = request.Name.Trim();
        row.Destination = request.Destination.Trim();
        row.IsEnabled = request.IsEnabled;
        row.EventTypesCsv = request.EventTypesCsv ?? string.Empty;
        row.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return new NotificationEndpointDto(row.Id, row.Channel.ToString(), row.Name, row.Destination, row.IsEnabled, row.EventTypesCsv);
    }
}

public sealed record GetSettingsQuery : IRequest<SettingsDto>;
public sealed class GetSettingsHandler : IRequestHandler<GetSettingsQuery, SettingsDto>
{
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.BackupOptions> _backup;
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.GitOptions> _git;
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> _jobs;
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.StorageOptions> _storage;
    public GetSettingsHandler(Microsoft.Extensions.Options.IOptions<Configuration.BackupOptions> backup, Microsoft.Extensions.Options.IOptions<Configuration.GitOptions> git, Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> jobs, Microsoft.Extensions.Options.IOptions<Configuration.StorageOptions> storage)
    {
        _backup = backup;
        _git = git;
        _jobs = jobs;
        _storage = storage;
    }
    public Task<SettingsDto> Handle(GetSettingsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new SettingsDto(_backup.Value.DailyRetentionDays, _backup.Value.MinimumFreeDiskPercentage, _backup.Value.EncryptionEnabled, _git.Value.Enabled, _git.Value.AllowHistoryRewrite, _storage.Value.Type.ToString(), _jobs.Value.MaxConcurrentBackups, _jobs.Value.MaxConcurrentMigrations));
}

public sealed record RotateEncryptionKeyCommand : IRequest<string>;
public sealed class RotateEncryptionKeyHandler : IRequestHandler<RotateEncryptionKeyCommand, string>
{
    private readonly IEncryptionService _encryption;
    public RotateEncryptionKeyHandler(IEncryptionService encryption) => _encryption = encryption;
    public Task<string> Handle(RotateEncryptionKeyCommand request, CancellationToken cancellationToken) =>
        ((Security.IKeyRotation)_encryption).RotateAsync(cancellationToken);
}
