using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Configuration;
using MySqlManager.Domain.Backups;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Operations;
using MySqlManager.Domain.Safety;
using Quartz;

namespace MySqlManager.Infrastructure.Operations;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class AuditWriter : IAuditWriter
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _current;
    private readonly IClock _clock;

    public AuditWriter(IApplicationDbContext db, ICurrentUser current, IClock clock)
    {
        _db = db;
        _current = current;
        _clock = clock;
    }

    public async Task WriteAsync(AuditWriteRequest request, CancellationToken cancellationToken)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = _current.UserId,
            Action = request.Action,
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            ServerId = request.ServerId,
            DatabaseId = request.DatabaseId,
            DestinationServerId = request.DestinationServerId,
            DestinationDatabase = request.DestinationDatabase,
            Timestamp = _clock.UtcNow,
            IpAddress = _current.IpAddress,
            Result = request.Result,
            Details = SecretRedactor.Clean(request.Details),
            SourceModification = request.SourceModification
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class JobProgressPublisher : IJobProgressPublisher
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;

    public JobProgressPublisher(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task PublishAsync(ProgressUpdate update, CancellationToken cancellationToken)
    {
        _db.JobProgressEvents.Add(new JobProgressEvent
        {
            JobId = update.JobId,
            JobType = update.JobType,
            Step = update.Step,
            State = update.State,
            Percentage = update.Percentage,
            Message = SecretRedactor.Clean(update.Message),
            CreatedAtUtc = _clock.UtcNow
        });
        _db.JobLogs.Add(new JobLog
        {
            JobId = update.JobId,
            JobType = update.JobType,
            Level = "Information",
            Message = $"{update.Step}: {SecretRedactor.Clean(update.Message)}",
            CreatedAtUtc = _clock.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class DatabaseDistributedLock : IDistributedLock
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;

    public DatabaseDistributedLock(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        var now = _clock.UtcNow;
        var existing = await _db.DistributedLocks.FirstOrDefaultAsync(l => l.LockName == name, cancellationToken);
        if (existing is null)
        {
            _db.DistributedLocks.Add(new DistributedLockRecord { LockName = name, Owner = owner, ExpiresAtUtc = now.Add(ttl) });
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return new Release(_db, name, owner);
            }
            catch
            {
                return null;
            }
        }

        if (existing.ExpiresAtUtc > now)
        {
            return null;
        }

        existing.Owner = owner;
        existing.ExpiresAtUtc = now.Add(ttl);
        await _db.SaveChangesAsync(cancellationToken);
        return new Release(_db, name, owner);
    }

    private sealed class Release : IAsyncDisposable
    {
        private readonly IApplicationDbContext _db;
        private readonly string _name;
        private readonly string _owner;
        public Release(IApplicationDbContext db, string name, string owner) { _db = db; _name = name; _owner = owner; }
        public async ValueTask DisposeAsync()
        {
            var row = await _db.DistributedLocks.FirstOrDefaultAsync(l => l.LockName == _name && l.Owner == _owner);
            if (row is not null)
            {
                _db.DistributedLocks.Remove(row);
                await _db.SaveChangesAsync();
            }
        }
    }
}

public sealed class RedisDistributedLock : IDistributedLock
{
    private readonly StackExchange.Redis.IConnectionMultiplexer _redis;
    public RedisDistributedLock(StackExchange.Redis.IConnectionMultiplexer redis) => _redis = redis;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        var db = _redis.GetDatabase();
        var acquired = await db.StringSetAsync("msm:" + name, owner, ttl, StackExchange.Redis.When.NotExists);
        return acquired ? new Release(db, name, owner) : null;
    }

    private sealed class Release : IAsyncDisposable
    {
        private readonly StackExchange.Redis.IDatabase _db;
        private readonly string _name;
        private readonly string _owner;
        public Release(StackExchange.Redis.IDatabase db, string name, string owner) { _db = db; _name = name; _owner = owner; }
        public async ValueTask DisposeAsync()
        {
            var current = await _db.StringGetAsync("msm:" + _name);
            if (current == _owner)
            {
                await _db.KeyDeleteAsync("msm:" + _name);
            }
        }
    }
}

public sealed class JobQueue : IJobQueue
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;

    public JobQueue(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<Guid?> TryLeaseBackupAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken) =>
        LeaseAsync(_db.BackupJobs, workerId, lease, cancellationToken);

    public Task<Guid?> TryLeaseMigrationAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken) =>
        LeaseCopyAsync(workerId, lease, cancellationToken);

    public Task<Guid?> TryLeaseRestoreAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken) =>
        LeaseRestoreAsync(workerId, lease, cancellationToken);

    private async Task<Guid?> LeaseAsync(IQueryable<Domain.Backups.BackupJob> jobs, string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var candidate = await jobs.Where(j => (j.Status == BackupJobStatus.Pending || (j.Status == BackupJobStatus.Running && j.LeaseUntilUtc < now)) && (j.NextAttemptAtUtc == null || j.NextAttemptAtUtc <= now))
            .OrderBy(j => j.CreatedAtUtc)
            .Select(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate == Guid.Empty)
        {
            return null;
        }

        var until = now.Add(lease);
        var updated = await jobs.Where(j => j.Id == candidate && (j.Status == BackupJobStatus.Pending || (j.Status == BackupJobStatus.Running && j.LeaseUntilUtc < now)))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedBy, workerId).SetProperty(j => j.LeaseUntilUtc, until).SetProperty(j => j.Status, BackupJobStatus.Running), cancellationToken);
        return updated == 1 ? candidate : null;
    }

    private async Task<Guid?> LeaseCopyAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var candidate = await _db.MigrationJobs.Where(j => j.Status == PipelineJobStatus.Pending && (j.NextAttemptAtUtc == null || j.NextAttemptAtUtc <= now) && (j.LockedBy == null || j.LeaseUntilUtc < now))
            .OrderBy(j => j.CreatedAtUtc).Select(j => j.Id).FirstOrDefaultAsync(cancellationToken);
        if (candidate == Guid.Empty) return null;
        var updated = await _db.MigrationJobs.Where(j => j.Id == candidate && j.Status == PipelineJobStatus.Pending && (j.LockedBy == null || j.LeaseUntilUtc < now))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedBy, workerId).SetProperty(j => j.LeaseUntilUtc, now.Add(lease)).SetProperty(j => j.Status, PipelineJobStatus.Preflight), cancellationToken);
        return updated == 1 ? candidate : null;
    }

    private async Task<Guid?> LeaseRestoreAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var candidate = await _db.RestoreJobs.Where(j => j.Status == PipelineJobStatus.Pending && (j.NextAttemptAtUtc == null || j.NextAttemptAtUtc <= now) && (j.LockedBy == null || j.LeaseUntilUtc < now))
            .OrderBy(j => j.CreatedAtUtc).Select(j => j.Id).FirstOrDefaultAsync(cancellationToken);
        if (candidate == Guid.Empty) return null;
        var updated = await _db.RestoreJobs.Where(j => j.Id == candidate && j.Status == PipelineJobStatus.Pending && (j.LockedBy == null || j.LeaseUntilUtc < now))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedBy, workerId).SetProperty(j => j.LeaseUntilUtc, now.Add(lease)).SetProperty(j => j.Status, PipelineJobStatus.Preflight), cancellationToken);
        return updated == 1 ? candidate : null;
    }
}

public sealed class RetentionService : IRetentionService
{
    private readonly IApplicationDbContext _db;
    private readonly IBackupStorage _storage;
    private readonly IDistributedLock _locks;
    private readonly IClock _clock;

    public RetentionService(IApplicationDbContext db, IBackupStorage storage, IDistributedLock locks, IClock clock)
    {
        _db = db;
        _storage = storage;
        _locks = locks;
        _clock = clock;
    }

    public async Task ApplyAsync(Guid serverId, Guid databaseId, int keepCount, CancellationToken cancellationToken)
    {
        await using var handle = await _locks.TryAcquireAsync($"retention:{serverId:N}:{databaseId:N}", TimeSpan.FromMinutes(5), cancellationToken);
        if (handle is null)
        {
            return;
        }

        var files = await _db.BackupFiles.Where(f => f.ServerId == serverId && f.DatabaseId == databaseId && f.RetentionClass == RetentionClass.Daily && f.Status == BackupFileStatus.Completed && f.VerificationStatus == VerificationStatus.Passed).ToListAsync(cancellationToken);
        foreach (var expired in RetentionPlanner.SelectExpired(files, file => file.CreatedAtUtc, keepCount))
        {
            _storage.Delete(expired.StoragePath);
            expired.Status = BackupFileStatus.Deleted;
            expired.IsDeleted = true;
            expired.UpdatedAtUtc = _clock.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class BackupScheduleEnqueuer : IBackupScheduleEnqueuer
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;
    private readonly IOptions<JobOptions> _jobs;

    public BackupScheduleEnqueuer(IApplicationDbContext db, IClock clock, IOptions<JobOptions> jobs)
    {
        _db = db;
        _clock = clock;
        _jobs = jobs;
    }

    public async Task EnqueueDueAsync(CancellationToken cancellationToken)
    {
        var schedules = await _db.BackupSchedules.Include(s => s.Configuration).Where(s => s.IsEnabled && s.Configuration.IsEnabled).ToListAsync(cancellationToken);
        foreach (var schedule in schedules)
        {
            if (!IsDue(schedule))
            {
                continue;
            }

            if (schedule.Configuration.DatabaseId == BackupConfiguration.AllDatabases)
            {
                var databases = await _db.Databases.Where(d => d.ServerId == schedule.Configuration.ServerId && !d.IsDeleted && d.IsActive).ToListAsync(cancellationToken);
                if (databases.Count == 0)
                {
                    continue;
                }

                foreach (var database in databases)
                {
                    var running = await _db.BackupJobs.AnyAsync(j => j.DatabaseId == database.Id && j.ServerId == schedule.Configuration.ServerId && (j.Status == BackupJobStatus.Pending || j.Status == BackupJobStatus.Running), cancellationToken);
                    if (!ConcurrencyGuard.CanStart(running))
                    {
                        continue;
                    }

                    _db.BackupJobs.Add(new Domain.Backups.BackupJob
                    {
                        ServerId = schedule.Configuration.ServerId,
                        DatabaseId = database.Id,
                        BackupConfigurationId = schedule.Configuration.Id,
                        Status = BackupJobStatus.Pending,
                        Trigger = JobTriggerKind.Schedule,
                        CreatedAtUtc = _clock.UtcNow,
                        MaxAttempts = _jobs.Value.MaxAttempts
                    });
                }

                schedule.LastEnqueuedAtUtc = _clock.UtcNow;
                continue;
            }

            var active = await _db.BackupJobs.AnyAsync(j => j.DatabaseId == schedule.Configuration.DatabaseId && j.ServerId == schedule.Configuration.ServerId && (j.Status == BackupJobStatus.Pending || j.Status == BackupJobStatus.Running), cancellationToken);
            if (!ConcurrencyGuard.CanStart(active))
            {
                continue;
            }

            _db.BackupJobs.Add(new Domain.Backups.BackupJob
            {
                ServerId = schedule.Configuration.ServerId,
                DatabaseId = schedule.Configuration.DatabaseId,
                BackupConfigurationId = schedule.Configuration.Id,
                Status = BackupJobStatus.Pending,
                Trigger = JobTriggerKind.Schedule,
                CreatedAtUtc = _clock.UtcNow,
                MaxAttempts = _jobs.Value.MaxAttempts
            });
            schedule.LastEnqueuedAtUtc = _clock.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private bool IsDue(Domain.Backups.BackupSchedule schedule)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        }
        catch
        {
            zone = TimeZoneInfo.Utc;
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(_clock.UtcNow, zone);
        if (schedule.Kind == ScheduleKind.DailyTime && schedule.DailyTime is not null)
        {
            if (localNow.TimeOfDay < schedule.DailyTime.Value.ToTimeSpan())
            {
                return false;
            }

            if (schedule.LastEnqueuedAtUtc is null)
            {
                return true;
            }

            var lastLocal = TimeZoneInfo.ConvertTimeFromUtc(schedule.LastEnqueuedAtUtc.Value, zone);
            return lastLocal.Date < localNow.Date;
        }

        if (schedule.Kind == ScheduleKind.Interval && schedule.IntervalHours is > 0)
        {
            return schedule.LastEnqueuedAtUtc is null || _clock.UtcNow - schedule.LastEnqueuedAtUtc.Value >= TimeSpan.FromHours(schedule.IntervalHours.Value);
        }

        if (schedule.Kind == ScheduleKind.Cron && !string.IsNullOrWhiteSpace(schedule.CronExpression) && CronExpression.IsValidExpression(schedule.CronExpression))
        {
            var cron = new CronExpression(schedule.CronExpression) { TimeZone = zone };
            var previous = cron.GetTimeAfter(new DateTimeOffset(_clock.UtcNow.AddMinutes(-1), TimeSpan.Zero));
            return previous is not null && previous.Value.UtcDateTime <= _clock.UtcNow && (schedule.LastEnqueuedAtUtc is null || schedule.LastEnqueuedAtUtc < previous.Value.UtcDateTime);
        }

        return false;
    }
}

public sealed class HealthProbe : IHealthProbe
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ISourceMySqlGateway _source;
    private readonly IBackupStorage _storage;
    private readonly INotificationDispatcher _notifications;
    private readonly IClock _clock;

    public HealthProbe(IApplicationDbContext db, ISecretProtector secrets, ISourceMySqlGateway source, IBackupStorage storage, INotificationDispatcher notifications, IClock clock)
    {
        _db = db;
        _secrets = secrets;
        _source = source;
        _storage = storage;
        _notifications = notifications;
        _clock = clock;
    }

    public async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        var disk = _storage.GetDiskUsage();
        Pipelines.AppMetrics.StorageFreeBytes = disk.FreeBytes;
        var servers = await _db.Servers.Include(s => s.Credentials).Where(s => s.IsActive).ToListAsync(cancellationToken);
        foreach (var server in servers)
        {
            var credential = server.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.BackupReadOnly && !c.IsDeleted);
            var lastBackup = await _db.BackupFiles.Where(f => f.ServerId == server.Id && f.Status == BackupFileStatus.Completed).OrderByDescending(f => f.CreatedAtUtc).Select(f => new { f.CreatedAtUtc, f.Status }).FirstOrDefaultAsync(cancellationToken);
            var lastMigration = await _db.MigrationJobs.Where(j => j.SourceServerId == server.Id && j.CompletedAtUtc != null).OrderByDescending(j => j.CompletedAtUtc).Select(j => j.CompletedAtUtc).FirstOrDefaultAsync(cancellationToken);
            var snapshot = new Domain.Servers.ServerHealthSnapshot
            {
                ServerId = server.Id,
                CapturedAtUtc = _clock.UtcNow,
                BackupStorageFreeBytes = disk.FreeBytes,
                BackupStorageTotalBytes = disk.TotalBytes,
                LastBackupAtUtc = lastBackup?.CreatedAtUtc,
                LastBackupStatus = lastBackup?.Status.ToString(),
                LastMigrationAtUtc = lastMigration
            };
            if (credential is null)
            {
                snapshot.IsOnline = false;
                snapshot.UnavailableReason = "No read-only credential.";
            }
            else
            {
                try
                {
                    var endpoint = await Application.Features.Servers.EndpointFactory.CreateAsync(server, credential, _secrets, ConnectionPurpose.BackupReadOnly, cancellationToken);
                    var facts = await _source.GetFactsAsync(endpoint, cancellationToken);
                    snapshot.IsOnline = facts.Connected;
                    snapshot.MySqlVersion = facts.MySqlVersion;
                    snapshot.UptimeSeconds = facts.UptimeSeconds;
                    snapshot.DatabaseCount = facts.DatabaseCount;
                    snapshot.LatencyMs = facts.LatencyMs;
                }
                catch (Exception ex)
                {
                    snapshot.IsOnline = false;
                    snapshot.UnavailableReason = SecretRedactor.Clean(ex.Message);
                    await _notifications.EnqueueAsync(NotificationEvents.ServerUnavailable, $"{server.Name} is unavailable.", cancellationToken);
                }
            }

            _db.ServerHealthSnapshots.Add(snapshot);
        }

        if (disk.FreePercentage < 15)
        {
            await _notifications.EnqueueAsync(NotificationEvents.LowDiskSpace, $"Backup volume free space is {disk.FreePercentage:0.0}%.", cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly IApplicationDbContext _db;
    private readonly IOptions<NotificationOptions> _options;
    private readonly IClock _clock;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public NotificationDispatcher(IApplicationDbContext db, IOptions<NotificationOptions> options, IClock clock)
    {
        _db = db;
        _options = options;
        _clock = clock;
    }

    public async Task EnqueueAsync(string eventType, string summary, CancellationToken cancellationToken)
    {
        var endpoints = await _db.NotificationEndpoints.Where(e => e.IsEnabled).ToListAsync(cancellationToken);
        foreach (var endpoint in endpoints.Where(e => string.IsNullOrWhiteSpace(e.EventTypesCsv) || e.EventTypesCsv.Contains(eventType, StringComparison.OrdinalIgnoreCase)))
        {
            _db.Notifications.Add(new Notification
            {
                Channel = endpoint.Channel,
                EventType = eventType,
                Recipient = endpoint.Destination,
                PayloadJson = JsonPayload(eventType, summary),
                CreatedAtUtc = _clock.UtcNow
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await _db.Notifications.Where(n => n.Status == NotificationStatus.Pending && n.Attempt < 3).OrderBy(n => n.CreatedAtUtc).Take(20).ToListAsync(cancellationToken);
        foreach (var notification in pending)
        {
            notification.Attempt += 1;
            try
            {
                if (notification.Channel == NotificationChannel.Email)
                {
                    await SendEmailAsync(notification, cancellationToken);
                }
                else
                {
                    using var content = new StringContent(notification.PayloadJson, System.Text.Encoding.UTF8, "application/json");
                    using var response = await _http.PostAsync(notification.Recipient, content, cancellationToken);
                    response.EnsureSuccessStatusCode();
                }

                notification.Status = NotificationStatus.Sent;
                notification.SentAtUtc = _clock.UtcNow;
            }
            catch (Exception ex)
            {
                notification.Status = notification.Attempt >= 3 ? NotificationStatus.Failed : NotificationStatus.Pending;
                notification.Error = SecretRedactor.Clean(ex.Message);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task SendEmailAsync(Notification notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.SmtpHost) || string.IsNullOrWhiteSpace(_options.Value.FromAddress))
        {
            throw new InvalidOperationException("SMTP is not configured.");
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.Value.FromAddress));
        message.To.Add(MailboxAddress.Parse(notification.Recipient));
        message.Subject = notification.EventType;
        message.Body = new TextPart("plain") { Text = notification.PayloadJson };
        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Value.SmtpHost, _options.Value.SmtpPort, _options.Value.SmtpUseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, cancellationToken);
        if (!string.IsNullOrWhiteSpace(_options.Value.SmtpUser))
        {
            await client.AuthenticateAsync(_options.Value.SmtpUser, _options.Value.SmtpPassword, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static string JsonPayload(string eventType, string summary) =>
        System.Text.Json.JsonSerializer.Serialize(new { eventType, summary, sourceModification = "NONE" });
}
