using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Contracts;
using MySqlManager.Application.Features.Servers;
using MySqlManager.Domain.Backups;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Safety;

namespace MySqlManager.Application.Features.Backups;

public sealed record UpsertBackupConfigurationCommand(
    Guid? Id,
    Guid ServerId,
    bool IncludeRoutines,
    bool IncludeTriggers,
    bool IncludeEvents,
    bool UseSingleTransaction,
    bool IsEnabled,
    int RetentionCount,
    string TimeZoneId,
    ScheduleKind ScheduleKind,
    string? DailyTime,
    int? IntervalHours,
    string? CronExpression,
    bool ScheduleEnabled) : IRequest<BackupConfigurationDto>;

public sealed class UpsertBackupConfigurationValidator : AbstractValidator<UpsertBackupConfigurationCommand>
{
    public UpsertBackupConfigurationValidator()
    {
        RuleFor(x => x.RetentionCount).InclusiveBetween(1, 30);
        RuleFor(x => x.TimeZoneId).NotEmpty();
        RuleFor(x => x.DailyTime).NotEmpty().When(x => x.ScheduleKind == ScheduleKind.DailyTime);
        RuleFor(x => x.IntervalHours).InclusiveBetween(1, 24).When(x => x.ScheduleKind == ScheduleKind.Interval);
        RuleFor(x => x.CronExpression).NotEmpty().When(x => x.ScheduleKind == ScheduleKind.Cron);
    }
}

public sealed class UpsertBackupConfigurationHandler : IRequestHandler<UpsertBackupConfigurationCommand, BackupConfigurationDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;

    public UpsertBackupConfigurationHandler(IApplicationDbContext db, IClock clock, ICurrentUser current)
    {
        _db = db;
        _clock = clock;
        _current = current;
    }

    public async Task<BackupConfigurationDto> Handle(UpsertBackupConfigurationCommand request, CancellationToken cancellationToken)
    {
        var serverExists = await _db.Servers.AnyAsync(s => s.Id == request.ServerId && !s.IsDeleted, cancellationToken);
        if (!serverExists)
        {
            throw new NotFoundException("Server was not found.");
        }

        var databases = await _db.Databases.Where(d => d.ServerId == request.ServerId && !d.IsDeleted && d.IsActive).ToListAsync(cancellationToken);
        if (databases.Count == 0)
        {
            throw new ConflictException("NO_DATABASES", "Discover the server databases before saving a schedule. The schedule covers every discovered database.");
        }

        if (request.ScheduleKind == ScheduleKind.Cron && !CronRules.LooksLikeCron(request.CronExpression))
        {
            throw new AppValidationException([new ApiFieldError("cronExpression", "Cron expression must contain 6 or 7 fields.")]);
        }

        TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        var config = request.Id is null
            ? null
            : await _db.BackupConfigurations.Include(c => c.Schedule).FirstOrDefaultAsync(c => c.Id == request.Id && !c.IsDeleted, cancellationToken);
        if (config is null)
        {
            config = await _db.BackupConfigurations.Include(c => c.Schedule)
                .FirstOrDefaultAsync(c => c.ServerId == request.ServerId && c.DatabaseId == BackupConfiguration.AllDatabases && !c.IsDeleted, cancellationToken);
        }
        if (config is null)
        {
            config = new BackupConfiguration { ServerId = request.ServerId, DatabaseId = BackupConfiguration.AllDatabases, CreatedAtUtc = _clock.UtcNow, CreatedBy = _current.UserId };
            _db.BackupConfigurations.Add(config);
        }

        var others = await _db.BackupConfigurations.Include(c => c.Schedule)
            .Where(c => c.ServerId == request.ServerId && c.Id != config.Id && c.DatabaseId != BackupConfiguration.AllDatabases && !c.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            other.IsEnabled = false;
            if (other.Schedule is not null)
            {
                other.Schedule.IsEnabled = false;
            }
        }

        config.DatabaseId = BackupConfiguration.AllDatabases;
        config.IncludeRoutines = request.IncludeRoutines;
        config.IncludeTriggers = request.IncludeTriggers;
        config.IncludeEvents = request.IncludeEvents;
        config.UseSingleTransaction = request.UseSingleTransaction;
        config.IsEnabled = request.IsEnabled;
        config.RetentionCount = request.RetentionCount;
        config.UpdatedAtUtc = _clock.UtcNow;
        config.UpdatedBy = _current.UserId;
        config.Schedule ??= new BackupSchedule { CreatedAtUtc = _clock.UtcNow };
        config.Schedule.TimeZoneId = request.TimeZoneId;
        config.Schedule.Kind = request.ScheduleKind;
        config.Schedule.DailyTime = string.IsNullOrWhiteSpace(request.DailyTime) ? null : TimeOnly.Parse(request.DailyTime);
        config.Schedule.IntervalHours = request.IntervalHours;
        config.Schedule.CronExpression = request.CronExpression;
        config.Schedule.IsEnabled = request.ScheduleEnabled;
        config.Schedule.UpdatedAtUtc = _clock.UtcNow;
        foreach (var database in databases)
        {
            database.IsSelectedForBackup = request.IsEnabled;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return BackupMapper.ToDto(config, "All databases");
    }
}

public sealed record ListBackupConfigurationsQuery : IRequest<IReadOnlyList<BackupConfigurationDto>>;
public sealed class ListBackupConfigurationsHandler : IRequestHandler<ListBackupConfigurationsQuery, IReadOnlyList<BackupConfigurationDto>>
{
    private readonly IApplicationDbContext _db;
    public ListBackupConfigurationsHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<BackupConfigurationDto>> Handle(ListBackupConfigurationsQuery request, CancellationToken cancellationToken)
    {
        var rows = await _db.BackupConfigurations.AsNoTracking().Include(c => c.Schedule).Where(c => !c.IsDeleted).ToListAsync(cancellationToken);
        var names = await _db.Databases.AsNoTracking().Where(d => rows.Select(r => r.DatabaseId).Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        return rows.Select(r => BackupMapper.ToDto(r, r.DatabaseId == BackupConfiguration.AllDatabases ? "All databases" : names.GetValueOrDefault(r.DatabaseId, string.Empty))).ToList();
    }
}

internal static class CronRules
{
    public static bool LooksLikeCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return false;
        }

        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is 6 or 7 && cron.IndexOfAny([';', '|', '&', '`']) < 0;
    }
}

public sealed record StartBackupCommand(Guid ServerId, Guid? DatabaseId) : IRequest<JobCreatedDto>;
public sealed class StartBackupHandler : IRequestHandler<StartBackupCommand, JobCreatedDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;
    private readonly IAuditWriter _audit;
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> _jobs;

    public StartBackupHandler(IApplicationDbContext db, IClock clock, ICurrentUser current, IAuditWriter audit, Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> jobs)
    {
        _db = db;
        _clock = clock;
        _current = current;
        _audit = audit;
        _jobs = jobs;
    }

    public async Task<JobCreatedDto> Handle(StartBackupCommand request, CancellationToken cancellationToken)
    {
        if (request.DatabaseId is null || request.DatabaseId == BackupConfiguration.AllDatabases)
        {
            var queued = await QueueAllDatabasesAsync(request.ServerId, JobTriggerKind.Manual, null, cancellationToken);
            return new JobCreatedDto(queued.FirstJobId, BackupJobStatus.Pending.ToString());
        }

        var database = await _db.Databases.FirstOrDefaultAsync(d => d.Id == request.DatabaseId && d.ServerId == request.ServerId && !d.IsDeleted && d.IsActive, cancellationToken)
            ?? throw new NotFoundException("Database was not found.");
        var active = await _db.BackupJobs.AnyAsync(j => j.ServerId == request.ServerId && j.DatabaseId == request.DatabaseId && (j.Status == BackupJobStatus.Pending || j.Status == BackupJobStatus.Running), cancellationToken);
        if (!ConcurrencyGuard.CanStart(active))
        {
            throw new ConflictException("BACKUP_ALREADY_RUNNING", "A backup for this server and database is already active.");
        }
        var config = await _db.BackupConfigurations.FirstOrDefaultAsync(c => c.ServerId == request.ServerId && (c.DatabaseId == request.DatabaseId || c.DatabaseId == BackupConfiguration.AllDatabases) && !c.IsDeleted, cancellationToken);
        var job = new BackupJob
        {
            ServerId = request.ServerId,
            DatabaseId = database.Id,
            BackupConfigurationId = config?.Id,
            Status = BackupJobStatus.Pending,
            Trigger = JobTriggerKind.Manual,
            CreatedAtUtc = _clock.UtcNow,
            CreatedBy = _current.UserId,
            MaxAttempts = _jobs.Value.MaxAttempts
        };
        _db.BackupJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.BackupStarted, "BackupJob", job.Id.ToString(), job.ServerId, job.DatabaseId, null, null, Domain.Enums.AuditResult.Success, "Backup job queued. Source database will remain unchanged."), cancellationToken);
        return new JobCreatedDto(job.Id, job.Status.ToString());
    }

    private async Task<(int Count, Guid FirstJobId)> QueueAllDatabasesAsync(Guid serverId, JobTriggerKind trigger, Guid? configurationId, CancellationToken cancellationToken)
    {
        var databases = await _db.Databases.Where(d => d.ServerId == serverId && !d.IsDeleted && d.IsActive).OrderBy(d => d.Name).ToListAsync(cancellationToken);
        if (databases.Count == 0)
        {
            throw new ConflictException("NO_DATABASES", "Discover the server databases before starting a backup. Each discovered database is queued separately.");
        }

        var configId = configurationId ?? (await _db.BackupConfigurations.Where(c => c.ServerId == serverId && c.DatabaseId == BackupConfiguration.AllDatabases && !c.IsDeleted).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken));
        Guid first = Guid.Empty;
        var count = 0;
        foreach (var database in databases)
        {
            var active = await _db.BackupJobs.AnyAsync(j => j.ServerId == serverId && j.DatabaseId == database.Id && (j.Status == BackupJobStatus.Pending || j.Status == BackupJobStatus.Running), cancellationToken);
            if (!ConcurrencyGuard.CanStart(active))
            {
                continue;
            }

            var job = new BackupJob
            {
                ServerId = serverId,
                DatabaseId = database.Id,
                BackupConfigurationId = configId,
                Status = BackupJobStatus.Pending,
                Trigger = trigger,
                CreatedAtUtc = _clock.UtcNow,
                CreatedBy = _current.UserId,
                MaxAttempts = _jobs.Value.MaxAttempts
            };
            _db.BackupJobs.Add(job);
            if (count == 0)
            {
                first = job.Id;
            }

            count++;
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.BackupStarted, "BackupJob", job.Id.ToString(), job.ServerId, job.DatabaseId, null, null, Domain.Enums.AuditResult.Success, "Backup job queued for every discovered database. Source database will remain unchanged."), cancellationToken);
        }

        if (count == 0)
        {
            throw new ConflictException("BACKUP_ALREADY_RUNNING", "A backup is already active for every discovered database.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (count, first);
    }
}

public sealed record ListBackupsQuery(Guid? ServerId, Guid? DatabaseId, string? Status, DateTime? FromUtc, DateTime? ToUtc) : IRequest<IReadOnlyList<BackupFileDto>>;
public sealed class ListBackupsHandler : IRequestHandler<ListBackupsQuery, IReadOnlyList<BackupFileDto>>
{
    private readonly IApplicationDbContext _db;
    public ListBackupsHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<BackupFileDto>> Handle(ListBackupsQuery request, CancellationToken cancellationToken)
    {
        var query = _db.BackupFiles.AsNoTracking().Where(f => !f.IsDeleted);
        if (request.ServerId is not null) query = query.Where(f => f.ServerId == request.ServerId);
        if (request.DatabaseId is not null) query = query.Where(f => f.DatabaseId == request.DatabaseId);
        if (request.FromUtc is not null) query = query.Where(f => f.CreatedAtUtc >= request.FromUtc);
        if (request.ToUtc is not null) query = query.Where(f => f.CreatedAtUtc <= request.ToUtc);
        if (!string.IsNullOrWhiteSpace(request.Status) && Enum.TryParse<BackupFileStatus>(request.Status, true, out var status))
        {
            query = query.Where(f => f.Status == status);
        }
        var rows = await query.OrderByDescending(f => f.CreatedAtUtc).Take(500).ToListAsync(cancellationToken);
        var servers = await _db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var databases = await _db.Databases.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        return rows.Select(file => BackupMapper.ToFileDto(file) with
        {
            ServerName = servers.GetValueOrDefault(file.ServerId) ?? string.Empty,
            DatabaseName = databases.GetValueOrDefault(file.DatabaseId) ?? string.Empty
        }).ToList();
    }
}

public sealed record GetBackupQuery(Guid Id) : IRequest<BackupFileDto>;
public sealed class GetBackupHandler : IRequestHandler<GetBackupQuery, BackupFileDto>
{
    private readonly IApplicationDbContext _db;
    public GetBackupHandler(IApplicationDbContext db) => _db = db;
    public async Task<BackupFileDto> Handle(GetBackupQuery request, CancellationToken cancellationToken)
    {
        var row = await _db.BackupFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == request.Id && !f.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Backup was not found.");
        return BackupMapper.ToFileDto(row);
    }
}

public sealed record DeleteBackupCommand(Guid Id, string Confirmation) : IRequest<bool>;
public sealed class DeleteBackupValidator : AbstractValidator<DeleteBackupCommand>
{
    public DeleteBackupValidator() => RuleFor(x => x.Confirmation).Equal(Confirmation.Delete);
}

public sealed class DeleteBackupHandler : IRequestHandler<DeleteBackupCommand, bool>
{
    private readonly IApplicationDbContext _db;
    private readonly IBackupStorage _storage;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public DeleteBackupHandler(IApplicationDbContext db, IBackupStorage storage, IAuditWriter audit, IClock clock)
    {
        _db = db;
        _storage = storage;
        _audit = audit;
        _clock = clock;
    }

    public async Task<bool> Handle(DeleteBackupCommand request, CancellationToken cancellationToken)
    {
        var file = await _db.BackupFiles.FirstOrDefaultAsync(f => f.Id == request.Id && !f.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Backup was not found.");
        _storage.Delete(file.StoragePath);
        file.Status = BackupFileStatus.Deleted;
        file.IsDeleted = true;
        file.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.BackupDeleted, "BackupFile", file.Id.ToString(), file.ServerId, file.DatabaseId, null, null, Domain.Enums.AuditResult.Success, "Deleted a backup artifact from application storage only."), cancellationToken);
        return true;
    }
}

public sealed record CancelJobCommand(Guid JobId, string JobType) : IRequest<bool>;
public sealed class CancelJobHandler : IRequestHandler<CancelJobCommand, bool>
{
    private readonly IApplicationDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public CancelJobHandler(IApplicationDbContext db, IAuditWriter audit, IClock clock)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
    }

    public async Task<bool> Handle(CancelJobCommand request, CancellationToken cancellationToken)
    {
        var kind = request.JobType.Trim().ToLowerInvariant();
        if (kind is "backup")
        {
            var job = await _db.BackupJobs.FirstOrDefaultAsync(j => j.Id == request.JobId, cancellationToken) ?? throw new NotFoundException("Job was not found.");
            if (job.Status is BackupJobStatus.Completed or BackupJobStatus.Failed or BackupJobStatus.Cancelled)
            {
                throw new ConflictException("JOB_NOT_CANCELLABLE", "The job is already finished.");
            }
            job.CancellationRequested = true;
            if (job.Status == BackupJobStatus.Pending)
            {
                job.Status = BackupJobStatus.Cancelled;
                job.CompletedAtUtc = _clock.UtcNow;
            }
        }
        else if (kind is "migration" or "clone" or "copy")
        {
            var job = await _db.MigrationJobs.FirstOrDefaultAsync(j => j.Id == request.JobId, cancellationToken) ?? throw new NotFoundException("Job was not found.");
            job.CancellationRequested = true;
            if (job.Status == PipelineJobStatus.Pending)
            {
                job.Status = PipelineJobStatus.Cancelled;
                job.CompletedAtUtc = _clock.UtcNow;
            }
        }
        else if (kind is "restore")
        {
            var job = await _db.RestoreJobs.FirstOrDefaultAsync(j => j.Id == request.JobId, cancellationToken) ?? throw new NotFoundException("Job was not found.");
            job.CancellationRequested = true;
            if (job.Status == PipelineJobStatus.Pending)
            {
                job.Status = PipelineJobStatus.Cancelled;
                job.CompletedAtUtc = _clock.UtcNow;
            }
        }
        else
        {
            throw new AppValidationException([new ApiFieldError("jobType", "Unsupported job type.")]);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.JobCancelled, "Job", request.JobId.ToString(), null, null, null, null, Domain.Enums.AuditResult.Success, "Cancellation requested. A cancelled job is not successful."), cancellationToken);
        return true;
    }
}

internal static class BackupMapper
{
    public static BackupConfigurationDto ToDto(BackupConfiguration config, string databaseName) => new(
        config.Id, config.ServerId, config.DatabaseId, databaseName, config.IncludeRoutines, config.IncludeTriggers, config.IncludeEvents, config.UseSingleTransaction, config.IsEnabled, config.RetentionCount,
        config.Schedule is null ? null : new BackupScheduleDto(config.Schedule.Id, config.Schedule.TimeZoneId, config.Schedule.Kind, config.Schedule.DailyTime?.ToString("HH:mm"), config.Schedule.IntervalHours, config.Schedule.CronExpression, config.Schedule.IsEnabled, config.Schedule.LastEnqueuedAtUtc));

    public static BackupFileDto ToFileDto(BackupFile file) => new(
        file.Id, file.ServerId, file.DatabaseId, file.BackupJobId, file.FileName, file.StoragePath, file.BackupType.ToString(), file.SizeBytes, file.CompressedSizeBytes, file.Checksum, file.EncryptionKeyId, file.EncryptionAlgorithm, file.CreatedAtUtc, file.CompletedAtUtc, file.Status.ToString(), file.VerificationStatus.ToString(), file.RetentionClass.ToString(), file.MySqlVersion);
}
