using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Configuration;
using MySqlManager.Application.Features.Git;
using MySqlManager.Application.Features.Servers;
using MySqlManager.Domain.Backups;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Migrations;
using MySqlManager.Domain.Operations;
using MySqlManager.Domain.Safety;

namespace MySqlManager.Infrastructure.Pipelines;

public static class AppMetrics
{
    public static readonly Meter Meter = new("MySqlManager");
    public static readonly Counter<long> BackupSuccess = Meter.CreateCounter<long>("backup_success_total");
    public static readonly Counter<long> BackupFailure = Meter.CreateCounter<long>("backup_failure_total");
    public static readonly Histogram<double> BackupDuration = Meter.CreateHistogram<double>("backup_duration_seconds");
    public static readonly Histogram<long> BackupSize = Meter.CreateHistogram<long>("backup_size_bytes");
    public static readonly Histogram<double> MigrationDuration = Meter.CreateHistogram<double>("migration_duration_seconds");
    public static readonly Counter<long> MigrationFailure = Meter.CreateCounter<long>("migration_failure_total");
    public static readonly ObservableGauge<long> StorageUsed = Meter.CreateObservableGauge("storage_used_bytes", () => StorageUsedBytes);
    public static readonly ObservableGauge<long> StorageFree = Meter.CreateObservableGauge("storage_free_bytes", () => StorageFreeBytes);
    public static long StorageUsedBytes;
    public static long StorageFreeBytes;
}

public sealed class BackupPipeline : IBackupPipeline
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ISourceMySqlGateway _source;
    private readonly IMySqlBackupService _dump;
    private readonly IEncryptionService _encryption;
    private readonly IBackupStorage _storage;
    private readonly IRetentionService _retention;
    private readonly IGitService _git;
    private readonly INotificationDispatcher _notifications;
    private readonly IAuditWriter _audit;
    private readonly IJobProgressPublisher _progress;
    private readonly IClock _clock;
    private readonly IOptions<BackupOptions> _options;
    private readonly IServiceScopeFactory _scopes;

    public BackupPipeline(IApplicationDbContext db, ISecretProtector secrets, ISourceMySqlGateway source, IMySqlBackupService dump, IEncryptionService encryption, IBackupStorage storage, IRetentionService retention, IGitService git, INotificationDispatcher notifications, IAuditWriter audit, IJobProgressPublisher progress, IClock clock, IOptions<BackupOptions> options, IServiceScopeFactory scopes)
    {
        _db = db;
        _secrets = secrets;
        _source = source;
        _dump = dump;
        _encryption = encryption;
        _storage = storage;
        _retention = retention;
        _git = git;
        _notifications = notifications;
        _audit = audit;
        _progress = progress;
        _clock = clock;
        _options = options;
        _scopes = scopes;
    }

    public async Task ExecuteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var started = StopwatchTimestamp();
        var job = await _db.BackupJobs.FirstAsync(j => j.Id == jobId, cancellationToken);
        var database = await _db.Databases.Include(d => d.Server).ThenInclude(s => s.Credentials).FirstAsync(d => d.Id == job.DatabaseId, cancellationToken);
        var config = job.BackupConfigurationId is null ? null : await _db.BackupConfigurations.FirstOrDefaultAsync(c => c.Id == job.BackupConfigurationId, cancellationToken);
        job.Attempt += 1;
        job.Status = BackupJobStatus.Running;
        job.StartedAtUtc ??= _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        var tempRoot = Path.Combine(Path.GetFullPath(_options.Value.TemporaryPath), job.Id.ToString("N"));
        Directory.CreateDirectory(tempRoot);
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = WatchAsync(job.Id, jobCancellation);
        try
        {
            if (job.CancellationRequested)
            {
                await FinishCancelledAsync(job, cancellationToken);
                return;
            }

            if (!_options.Value.EncryptionEnabled)
            {
                throw new PermanentJobException(PermanentErrorCode.Validation, "Encryption is required before a backup can be stored.");
            }

            var credential = database.Server.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.BackupReadOnly && !c.IsDeleted)
                ?? throw new PermanentJobException(PermanentErrorCode.PrivilegeFailure, "No read-only backup credential is configured.");
            var endpoint = await EndpointFactory.CreateAsync(database.Server, credential, _secrets, ConnectionPurpose.BackupReadOnly, jobCancellation.Token);
            var grants = await _source.GetGrantsAsync(endpoint, jobCancellation.Token);
            var analysis = MySqlGrantAnalyzer.Analyze(grants, true);
            if (!analysis.IsAcceptable)
            {
                throw new PermanentJobException(PermanentErrorCode.PrivilegeFailure, analysis.Summary);
            }

            var disk = _storage.GetDiskUsage();
            AppMetrics.StorageFreeBytes = disk.FreeBytes;
            var required = Math.Max(database.SizeBytes, 64L * 1024 * 1024);
            if (disk.FreePercentage < _options.Value.MinimumFreeDiskPercentage || disk.FreeBytes < required)
            {
                await _notifications.EnqueueAsync(NotificationEvents.LowDiskSpace, $"Backup storage free space is {disk.FreePercentage:0.0}%.", cancellationToken);
                throw new PermanentJobException(PermanentErrorCode.InsufficientDiskSpace, "INSUFFICIENT_DISK_SPACE");
            }

            await _progress.PublishAsync(new ProgressUpdate(job.Id, JobKind.Backup, "Dump", "Running", null, "Streaming mysqldump."), jobCancellation.Token);
            var gzipPath = Path.Combine(tempRoot, "dump.sql.gz");
            var encryptedPath = Path.Combine(tempRoot, "dump.sql.gz.enc");
            var dump = await _dump.DumpAsync(new DumpRequest(endpoint, database.Name, config?.UseSingleTransaction ?? true, config?.IncludeRoutines ?? true, config?.IncludeTriggers ?? true, config?.IncludeEvents ?? true, gzipPath), jobCancellation.Token);
            var keyId = await _encryption.EncryptFileAsync(gzipPath, encryptedPath, jobCancellation.Token);
            File.Delete(gzipPath);
            var checksum = await _encryption.ComputeSha256Async(encryptedPath, jobCancellation.Token);
            await _encryption.VerifyAsync(encryptedPath, keyId, checksum, jobCancellation.Token);
            var relative = BackupPathRules.BuildRelativeDirectory(database.Server.Name);
            var fileName = BackupPathRules.BuildFileName(database.Name, _clock.UtcNow);
            var finalPath = _storage.Promote(encryptedPath, relative, fileName);
            var info = new FileInfo(finalPath);
            var backupFile = new BackupFile
            {
                ServerId = database.ServerId,
                DatabaseId = database.Id,
                BackupJobId = job.Id,
                FileName = fileName,
                StoragePath = finalPath,
                SizeBytes = database.SizeBytes,
                CompressedSizeBytes = info.Length,
                Checksum = checksum,
                EncryptionKeyId = keyId,
                CreatedAtUtc = _clock.UtcNow,
                CompletedAtUtc = _clock.UtcNow,
                Status = BackupFileStatus.Completed,
                VerificationStatus = VerificationStatus.Passed,
                MySqlVersion = dump.MySqlVersion,
                CreatedBy = job.CreatedBy
            };
            _db.BackupFiles.Add(backupFile);
            job.Status = BackupJobStatus.Completed;
            job.CompletedAtUtc = _clock.UtcNow;
            job.CompressedBytes = info.Length;
            job.UncompressedBytes = database.SizeBytes;
            job.ErrorMessage = null;
            await _db.SaveChangesAsync(cancellationToken);
            if (!BackupSafetyRules.MayDeleteOlderBackups(job.Status, backupFile.VerificationStatus))
            {
                throw new InvalidOperationException("Retention cannot run before verification.");
            }

            await _retention.ApplyAsync(database.ServerId, database.Id, config?.RetentionCount ?? _options.Value.DailyRetentionDays, cancellationToken);
            await SyncGitAsync(job, database, backupFile, cancellationToken);
            await _notifications.EnqueueAsync(NotificationEvents.BackupSuccess, $"Backup completed for {database.Server.Name}/{database.Name}.", cancellationToken);
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.BackupCompleted, "BackupJob", job.Id.ToString(), job.ServerId, job.DatabaseId, null, null, Domain.Enums.AuditResult.Success, "Backup verified. Source modification: NONE."), cancellationToken);
            AppMetrics.BackupSuccess.Add(1);
            AppMetrics.BackupSize.Record(info.Length);
            AppMetrics.BackupDuration.Record(ElapsedSeconds(started));
            await _progress.PublishAsync(new ProgressUpdate(job.Id, JobKind.Backup, "Completed", "Passed", 100, "Backup verified."), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await FinishCancelledAsync(job, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await FailAsync(job, ex, started, database.Server.Name, database.Name, cancellationToken);
        }
        finally
        {
            jobCancellation.Cancel();
            try { await watcher; } catch { }
            CleanupTemp(tempRoot);
        }
    }

    private async Task SyncGitAsync(BackupJob job, Domain.Servers.ManagedDatabase database, BackupFile backupFile, CancellationToken cancellationToken)
    {
        var repo = await _db.GitRepositories.Where(r => r.IsEnabled && !r.IsDeleted).OrderByDescending(r => r.UpdatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (repo is null)
        {
            job.GitSyncStatus = GitSyncStatus.NotRequested;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var retained = await _db.BackupFiles.Where(f => f.ServerId == database.ServerId && f.DatabaseId == database.Id && f.Status == BackupFileStatus.Completed && f.VerificationStatus == VerificationStatus.Passed)
                .OrderByDescending(f => f.CreatedAtUtc).Take(7).Select(f => f.FileName).ToListAsync(cancellationToken);
            var manifest = ManifestBuilder.Build(database.Server.Name, database.Name, backupFile.CreatedAtUtc, backupFile.MySqlVersion, backupFile.SizeBytes, backupFile.CompressedSizeBytes, backupFile.Checksum, backupFile.EncryptionKeyId, retained);
            var work = await GitWorkFactory.CreateAsync(repo, database.Server.Name, database.Name, repo.Mode == GitMode.MetadataOnly ? null : backupFile.StoragePath, backupFile.FileName, manifest, retained, _secrets, cancellationToken);
            var sha = await _git.SyncAsync(work, cancellationToken);
            repo.LastCommitSha = sha;
            repo.LastPushAtUtc = _clock.UtcNow;
            repo.LastError = null;
            job.GitSyncStatus = GitSyncStatus.Succeeded;
            var serverFolder = BackupPathRules.SanitizeSegment(database.Server.Name);
            var databaseFolder = BackupPathRules.SanitizeSegment(database.Name);
            _db.GitBackupRecords.Add(new Domain.Git.GitBackupRecord { GitRepositoryId = repo.Id, BackupFileId = backupFile.Id, RelativePath = $"{serverFolder}/{databaseFolder}/daily/{backupFile.FileName}", CommitSha = sha, Status = GitSyncStatus.Succeeded, PushedAtUtc = _clock.UtcNow, CreatedAtUtc = _clock.UtcNow });
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.GitPush, "GitRepository", repo.Id.ToString(), database.ServerId, database.Id, null, null, Domain.Enums.AuditResult.Success, "Git backup sync pushed the current retention set."), cancellationToken);
        }
        catch (Exception ex)
        {
            job.GitSyncStatus = GitSyncStatus.Failed;
            job.GitSyncError = SecretRedactor.Clean(ex.Message);
            repo.LastError = job.GitSyncError;
            await _notifications.EnqueueAsync(NotificationEvents.GitPushFailure, job.GitSyncError, cancellationToken);
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.GitPush, "GitRepository", repo.Id.ToString(), database.ServerId, database.Id, null, null, Domain.Enums.AuditResult.Failure, job.GitSyncError), cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task FailAsync(BackupJob job, Exception ex, long started, string server, string database, CancellationToken cancellationToken)
    {
        var permanent = ex is PermanentJobException || ex is SourceSafetyException;
        var code = ex is PermanentJobException permanentEx ? permanentEx.Code : PermanentErrorCode.None;
        job.ErrorMessage = SecretRedactor.Clean(ex.Message);
        job.ErrorCode = code;
        if (!permanent && RetrySchedule.ShouldRetry(false, job.Attempt, job.MaxAttempts))
        {
            job.Status = BackupJobStatus.Pending;
            job.NextAttemptAtUtc = _clock.UtcNow.Add(RetrySchedule.DelayForAttempt(job.Attempt));
            job.LockedBy = null;
            job.LeaseUntilUtc = null;
        }
        else
        {
            job.Status = BackupJobStatus.Failed;
            job.CompletedAtUtc = _clock.UtcNow;
            AppMetrics.BackupFailure.Add(1);
            await _notifications.EnqueueAsync(NotificationEvents.BackupFailure, $"Backup failed for {server}/{database}: {job.ErrorMessage}", cancellationToken);
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.BackupFailed, "BackupJob", job.Id.ToString(), job.ServerId, job.DatabaseId, null, null, Domain.Enums.AuditResult.Failure, job.ErrorMessage), cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        AppMetrics.BackupDuration.Record(ElapsedSeconds(started));
        await _progress.PublishAsync(new ProgressUpdate(job.Id, JobKind.Backup, "Failed", job.Status.ToString(), null, job.ErrorMessage ?? "Backup failed."), CancellationToken.None);
    }

    private async Task FinishCancelledAsync(BackupJob job, CancellationToken cancellationToken)
    {
        job.Status = BackupJobStatus.Cancelled;
        job.CompletedAtUtc = _clock.UtcNow;
        job.ErrorMessage = "Cancelled. Previous verified backups were preserved.";
        await _db.SaveChangesAsync(cancellationToken);
        await _progress.PublishAsync(new ProgressUpdate(job.Id, JobKind.Backup, "Cancelled", "Cancelled", null, job.ErrorMessage), CancellationToken.None);
    }

    private async Task WatchAsync(Guid jobId, CancellationTokenSource cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                var requested = await db.BackupJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.CancellationRequested).FirstAsync(cancellation.Token);
                await db.BackupJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(10)), cancellation.Token);
                if (requested)
                {
                    cancellation.Cancel();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private static void CleanupTemp(string tempRoot)
    {
        try
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
        catch
        {
        }
    }

    private static long StopwatchTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
    private static double ElapsedSeconds(long started) => System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
}

public sealed class CopyPipeline : ICopyPipeline
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ISourceMySqlGateway _source;
    private readonly IDestinationMySqlGateway _destination;
    private readonly IMySqlBackupService _dump;
    private readonly IMySqlImportService _import;
    private readonly ICompressionService _compression;
    private readonly IBackupStorage _storage;
    private readonly INotificationDispatcher _notifications;
    private readonly IAuditWriter _audit;
    private readonly IJobProgressPublisher _progress;
    private readonly IClock _clock;
    private readonly IOptions<BackupOptions> _options;

    public CopyPipeline(IApplicationDbContext db, ISecretProtector secrets, ISourceMySqlGateway source, IDestinationMySqlGateway destination, IMySqlBackupService dump, IMySqlImportService import, ICompressionService compression, IBackupStorage storage, INotificationDispatcher notifications, IAuditWriter audit, IJobProgressPublisher progress, IClock clock, IOptions<BackupOptions> options)
    {
        _db = db;
        _secrets = secrets;
        _source = source;
        _destination = destination;
        _dump = dump;
        _import = import;
        _compression = compression;
        _storage = storage;
        _notifications = notifications;
        _audit = audit;
        _progress = progress;
        _clock = clock;
        _options = options;
    }

    public async Task ExecuteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var job = await _db.MigrationJobs.FirstAsync(j => j.Id == jobId, cancellationToken);
        job.Attempt += 1;
        job.StartedAtUtc ??= _clock.UtcNow;
        job.Status = PipelineJobStatus.Preflight;
        job.CurrentStep = "Preflight";
        await _db.SaveChangesAsync(cancellationToken);
        var tempRoot = Path.Combine(Path.GetFullPath(_options.Value.TemporaryPath), "copy-" + job.Id.ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var sourceDatabase = await _db.Databases.Include(d => d.Server).ThenInclude(s => s.Credentials).FirstAsync(d => d.Id == job.SourceDatabaseId, cancellationToken);
            var destinationServer = await _db.Servers.Include(s => s.Credentials).FirstAsync(s => s.Id == job.DestinationServerId, cancellationToken);
            var sourceCredential = sourceDatabase.Server.Credentials.First(c => c.Purpose == CredentialPurpose.BackupReadOnly && !c.IsDeleted);
            var destinationCredential = destinationServer.Credentials.First(c => c.Purpose == CredentialPurpose.DestinationWrite && !c.IsDeleted);
            var sourceEndpoint = await EndpointFactory.CreateAsync(sourceDatabase.Server, sourceCredential, _secrets, ConnectionPurpose.BackupReadOnly, cancellationToken);
            var destinationEndpoint = await EndpointFactory.CreateAsync(destinationServer, destinationCredential, _secrets, ConnectionPurpose.DestinationWrite, cancellationToken);
            if (await _destination.DatabaseExistsAsync(destinationEndpoint, job.DestinationDatabaseName, cancellationToken))
            {
                throw new PermanentJobException(PermanentErrorCode.DestinationAlreadyExists, $"Destination database {job.DestinationDatabaseName} already exists and will not be overwritten.");
            }

            var disk = _storage.GetDiskUsage();
            if (disk.FreePercentage < _options.Value.MinimumFreeDiskPercentage)
            {
                throw new PermanentJobException(PermanentErrorCode.InsufficientDiskSpace, "INSUFFICIENT_DISK_SPACE");
            }

            job.Status = PipelineJobStatus.Dumping;
            job.CurrentStep = "Dump";
            job.TotalBytes = sourceDatabase.SizeBytes > 0 ? sourceDatabase.SizeBytes : null;
            await _db.SaveChangesAsync(cancellationToken);
            await _progress.PublishAsync(new ProgressUpdate(job.Id, job.Mode == CopyMode.Clone ? JobKind.Clone : JobKind.Migration, "Dump", "Running", null, "Reading the source database."), cancellationToken);
            var gzipPath = Path.Combine(tempRoot, "copy.sql.gz");
            await _dump.DumpAsync(new DumpRequest(sourceEndpoint, sourceDatabase.Name, true, true, true, true, gzipPath), cancellationToken);
            var sourceInventory = await _source.GetInventoryAsync(sourceEndpoint, sourceDatabase.Name, job.ValidationLevel == ValidationLevel.Deep, cancellationToken);
            job.Status = PipelineJobStatus.CreatingDatabase;
            job.CurrentStep = "CreateDatabase";
            await _db.SaveChangesAsync(cancellationToken);
            var charset = string.IsNullOrWhiteSpace(sourceInventory.Charset) ? "utf8mb4" : sourceInventory.Charset;
            var collation = string.IsNullOrWhiteSpace(sourceInventory.Collation) ? "utf8mb4_unicode_ci" : sourceInventory.Collation;
            await _destination.CreateDatabaseAsync(destinationEndpoint, job.DestinationDatabaseName, charset, collation, cancellationToken);
            job.Status = PipelineJobStatus.Importing;
            job.CurrentStep = "Import";
            await _db.SaveChangesAsync(cancellationToken);
            await using (var file = File.OpenRead(gzipPath))
            await using (var sql = _compression.OpenDecompress(file))
            {
                var progress = new Progress<long>(value =>
                {
                    job.TransferredBytes = value;
                    job.ProgressPercentage = job.TotalBytes is > 0 ? Math.Min(99, value * 100d / job.TotalBytes.Value) : null;
                });
                await _import.ImportAsync(destinationEndpoint, job.DestinationDatabaseName, sql, progress, cancellationToken);
            }

            job.Status = PipelineJobStatus.Validating;
            job.CurrentStep = "Validate";
            await _db.SaveChangesAsync(cancellationToken);
            var destinationInventory = await _destination.GetInventoryAsync(destinationEndpoint, job.DestinationDatabaseName, job.ValidationLevel == ValidationLevel.Deep, cancellationToken);
            var passed = Compare(sourceInventory, destinationInventory, job.ValidationLevel);
            job.ValidationStatus = passed ? VerificationStatus.Passed : VerificationStatus.Failed;
            job.Status = passed ? PipelineJobStatus.Completed : PipelineJobStatus.Failed;
            job.CurrentStep = passed ? "Completed" : "ValidationFailed";
            job.CompletedAtUtc = _clock.UtcNow;
            job.ProgressPercentage = passed ? 100 : job.ProgressPercentage;
            job.SourceModification = "NONE";
            job.ReportJson = JsonSerializer.Serialize(new
            {
                source = new { server = sourceDatabase.Server.Name, database = sourceDatabase.Name, sizeBytes = sourceDatabase.SizeBytes },
                destination = new { server = destinationServer.Name, database = job.DestinationDatabaseName },
                sourceModification = "NONE",
                duration = System.Diagnostics.Stopwatch.GetElapsedTime(started).ToString(),
                tables = new { source = sourceInventory.Tables, destination = destinationInventory.Tables },
                views = new { source = sourceInventory.Views, destination = destinationInventory.Views },
                procedures = new { source = sourceInventory.Procedures, destination = destinationInventory.Procedures },
                triggers = new { source = sourceInventory.Triggers, destination = destinationInventory.Triggers },
                validation = job.ValidationStatus.ToString()
            });
            _db.MigrationValidations.Add(new MigrationValidation { MigrationJobId = job.Id, CheckName = "DestinationInventory", Passed = passed, Details = job.ReportJson, CapturedAtUtc = _clock.UtcNow });
            await _db.SaveChangesAsync(cancellationToken);
            if (!passed)
            {
                throw new PermanentJobException(PermanentErrorCode.Validation, "Destination validation did not match the source. The source database was not modified and the existing destination was not dropped.");
            }

            await _notifications.EnqueueAsync(NotificationEvents.MigrationSuccess, $"Copy completed to {destinationServer.Name}/{job.DestinationDatabaseName}. Source unchanged.", cancellationToken);
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.MigrationCompleted, "MigrationJob", job.Id.ToString(), job.SourceServerId, job.SourceDatabaseId, job.DestinationServerId, job.DestinationDatabaseName, Domain.Enums.AuditResult.Success, "Source modification: NONE"), cancellationToken);
            AppMetrics.MigrationDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
            await _progress.PublishAsync(new ProgressUpdate(job.Id, JobKind.Migration, "Completed", "Passed", 100, "Copy validated. Source unchanged."), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailCopyAsync(job, ex, started, cancellationToken);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private async Task FailCopyAsync(MigrationJob job, Exception ex, long started, CancellationToken cancellationToken)
    {
        job.ErrorMessage = SecretRedactor.Clean(ex.Message);
        job.ErrorCode = ex is PermanentJobException permanent ? permanent.Code : PermanentErrorCode.None;
        var permanentFailure = ex is PermanentJobException || ex is SourceSafetyException || ex is DestinationExistsException;
        if (!permanentFailure && RetrySchedule.ShouldRetry(false, job.Attempt, job.MaxAttempts) && job.Status < PipelineJobStatus.CreatingDatabase)
        {
            job.Status = PipelineJobStatus.Pending;
            job.NextAttemptAtUtc = _clock.UtcNow.Add(RetrySchedule.DelayForAttempt(job.Attempt));
            job.LockedBy = null;
        }
        else
        {
            job.Status = PipelineJobStatus.Failed;
            job.CompletedAtUtc = _clock.UtcNow;
            job.SourceModification = "NONE";
            AppMetrics.MigrationFailure.Add(1);
            await _notifications.EnqueueAsync(NotificationEvents.MigrationFailure, job.ErrorMessage, cancellationToken);
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.MigrationFailed, "MigrationJob", job.Id.ToString(), job.SourceServerId, job.SourceDatabaseId, job.DestinationServerId, job.DestinationDatabaseName, Domain.Enums.AuditResult.Failure, job.ErrorMessage + " Source modification: NONE"), cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        AppMetrics.MigrationDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    public static bool Compare(SchemaInventory source, SchemaInventory destination, ValidationLevel level)
    {
        if (!destination.Exists)
        {
            return false;
        }

        if (level == ValidationLevel.Basic)
        {
            return true;
        }

        var countsMatch = source.Tables == destination.Tables && source.Views == destination.Views && source.Procedures == destination.Procedures && source.Triggers == destination.Triggers && source.Events == destination.Events;
        if (!countsMatch || level != ValidationLevel.Deep)
        {
            return countsMatch;
        }

        foreach (var sample in source.Checksums)
        {
            var match = destination.Checksums.FirstOrDefault(c => c.Table == sample.Table);
            if (match is null || match.Checksum != sample.Checksum)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class RestorePipeline : IRestorePipeline
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IDestinationMySqlGateway _destination;
    private readonly IMySqlImportService _import;
    private readonly IEncryptionService _encryption;
    private readonly ICompressionService _compression;
    private readonly INotificationDispatcher _notifications;
    private readonly IAuditWriter _audit;
    private readonly IJobProgressPublisher _progress;
    private readonly IClock _clock;

    public RestorePipeline(IApplicationDbContext db, ISecretProtector secrets, IDestinationMySqlGateway destination, IMySqlImportService import, IEncryptionService encryption, ICompressionService compression, INotificationDispatcher notifications, IAuditWriter audit, IJobProgressPublisher progress, IClock clock)
    {
        _db = db;
        _secrets = secrets;
        _destination = destination;
        _import = import;
        _encryption = encryption;
        _compression = compression;
        _notifications = notifications;
        _audit = audit;
        _progress = progress;
        _clock = clock;
    }

    public async Task ExecuteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.RestoreJobs.FirstAsync(j => j.Id == jobId, cancellationToken);
        var backup = await _db.BackupFiles.FirstAsync(f => f.Id == job.BackupFileId, cancellationToken);
        var destination = await _db.Servers.Include(s => s.Credentials).FirstAsync(s => s.Id == job.DestinationServerId, cancellationToken);
        job.Attempt += 1;
        job.StartedAtUtc ??= _clock.UtcNow;
        job.Status = PipelineJobStatus.Preflight;
        await _db.SaveChangesAsync(cancellationToken);
        try
        {
            if (!BackupSafetyRules.IsValidRestorePoint(backup.Status, backup.VerificationStatus))
            {
                throw new PermanentJobException(PermanentErrorCode.ChecksumMismatch, "The backup is not a verified restore point.");
            }

            await _encryption.VerifyAsync(backup.StoragePath, backup.EncryptionKeyId, backup.Checksum, cancellationToken);
            var credential = destination.Credentials.First(c => c.Purpose == CredentialPurpose.DestinationWrite && !c.IsDeleted);
            var endpoint = await EndpointFactory.CreateAsync(destination, credential, _secrets, ConnectionPurpose.DestinationWrite, cancellationToken);
            if (await _destination.DatabaseExistsAsync(endpoint, job.DestinationDatabaseName, cancellationToken))
            {
                throw new PermanentJobException(PermanentErrorCode.DestinationAlreadyExists, "Destination database already exists and will not be overwritten.");
            }

            var sourceDatabase = await _db.Databases.FirstAsync(d => d.Id == backup.DatabaseId, cancellationToken);
            job.Status = PipelineJobStatus.CreatingDatabase;
            await _db.SaveChangesAsync(cancellationToken);
            var charset = string.IsNullOrWhiteSpace(sourceDatabase.Charset) ? "utf8mb4" : sourceDatabase.Charset;
            var collation = string.IsNullOrWhiteSpace(sourceDatabase.Collation) ? "utf8mb4_unicode_ci" : sourceDatabase.Collation;
            await _destination.CreateDatabaseAsync(endpoint, job.DestinationDatabaseName, charset, collation, cancellationToken);
            job.Status = PipelineJobStatus.Importing;
            job.TotalBytes = backup.CompressedSizeBytes;
            await _db.SaveChangesAsync(cancellationToken);
            await using (var encrypted = _encryption.OpenDecrypt(backup.StoragePath, backup.EncryptionKeyId))
            await using (var sql = _compression.OpenDecompress(encrypted))
            {
                await _import.ImportAsync(endpoint, job.DestinationDatabaseName, sql, new Progress<long>(value => job.TransferredBytes = value), cancellationToken);
            }

            job.Status = PipelineJobStatus.Validating;
            await _db.SaveChangesAsync(cancellationToken);
            var inventory = await _destination.GetInventoryAsync(endpoint, job.DestinationDatabaseName, job.ValidationLevel == ValidationLevel.Deep, cancellationToken);
            if (!inventory.Exists)
            {
                throw new PermanentJobException(PermanentErrorCode.Validation, "Destination database was not found after import.");
            }

            job.Status = PipelineJobStatus.Completed;
            job.ValidationStatus = VerificationStatus.Passed;
            job.CompletedAtUtc = _clock.UtcNow;
            job.ProgressPercentage = 100;
            job.CurrentStep = "Completed";
            job.SourceModification = "NONE";
            job.ReportJson = JsonSerializer.Serialize(new
            {
                backupFile = backup.FileName,
                checksum = backup.Checksum,
                destination = new { server = destination.Name, database = job.DestinationDatabaseName },
                tables = inventory.Tables,
                views = inventory.Views,
                procedures = inventory.Procedures,
                triggers = inventory.Triggers,
                sourceModification = "NONE",
                validation = "PASSED"
            });
            await _db.SaveChangesAsync(cancellationToken);
            await _notifications.EnqueueAsync(NotificationEvents.RestoreSuccess, $"Restore completed into {destination.Name}/{job.DestinationDatabaseName}.", cancellationToken);
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.RestoreCompleted, "RestoreJob", job.Id.ToString(), backup.ServerId, backup.DatabaseId, destination.Id, job.DestinationDatabaseName, Domain.Enums.AuditResult.Success, "Source modification: NONE"), cancellationToken);
            await _progress.PublishAsync(new ProgressUpdate(job.Id, JobKind.Restore, "Completed", "Passed", 100, "Restore validated."), cancellationToken);
        }
        catch (Exception ex)
        {
            job.Status = PipelineJobStatus.Failed;
            job.ValidationStatus = VerificationStatus.Failed;
            job.CompletedAtUtc = _clock.UtcNow;
            job.ErrorMessage = SecretRedactor.Clean(ex.Message);
            job.ErrorCode = ex is PermanentJobException permanent ? permanent.Code : PermanentErrorCode.None;
            job.SourceModification = "NONE";
            await _db.SaveChangesAsync(cancellationToken);
            await _notifications.EnqueueAsync(NotificationEvents.RestoreFailure, job.ErrorMessage, cancellationToken);
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.RestoreFailed, "RestoreJob", job.Id.ToString(), backup.ServerId, backup.DatabaseId, job.DestinationServerId, job.DestinationDatabaseName, Domain.Enums.AuditResult.Failure, job.ErrorMessage), cancellationToken);
        }
    }
}
