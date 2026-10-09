using MySqlManager.Domain.Common;
using MySqlManager.Domain.Enums;

namespace MySqlManager.Domain.Backups;

public sealed class BackupConfiguration : AuditableEntity
{
    public const string AllDatabasesName = "All databases";
    public static readonly Guid AllDatabases = Guid.Empty;
    public Guid ServerId { get; set; }
    public Guid DatabaseId { get; set; }
    public bool IncludeRoutines { get; set; } = true;
    public bool IncludeTriggers { get; set; } = true;
    public bool IncludeEvents { get; set; } = true;
    public bool UseSingleTransaction { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public int RetentionCount { get; set; } = 7;
    public BackupSchedule? Schedule { get; set; }
}

public sealed class BackupSchedule : AuditableEntity
{
    public Guid BackupConfigurationId { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public ScheduleKind Kind { get; set; } = ScheduleKind.DailyTime;
    public TimeOnly? DailyTime { get; set; }
    public int? IntervalHours { get; set; }
    public string? CronExpression { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastEnqueuedAtUtc { get; set; }
    public BackupConfiguration Configuration { get; set; } = null!;
}

public sealed class BackupJob : AuditableEntity
{
    public Guid ServerId { get; set; }
    public Guid DatabaseId { get; set; }
    public Guid? BackupConfigurationId { get; set; }
    public BackupJobStatus Status { get; set; } = BackupJobStatus.Pending;
    public JobTriggerKind Trigger { get; set; } = JobTriggerKind.Manual;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public PermanentErrorCode ErrorCode { get; set; } = PermanentErrorCode.None;
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public bool CancellationRequested { get; set; }
    public GitSyncStatus GitSyncStatus { get; set; } = GitSyncStatus.NotRequested;
    public string? GitSyncError { get; set; }
    public long? UncompressedBytes { get; set; }
    public long? CompressedBytes { get; set; }
}

public sealed class BackupFile : AuditableEntity
{
    public Guid ServerId { get; set; }
    public Guid DatabaseId { get; set; }
    public Guid BackupJobId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public BackupType BackupType { get; set; } = BackupType.Full;
    public long SizeBytes { get; set; }
    public long CompressedSizeBytes { get; set; }
    public string Checksum { get; set; } = string.Empty;
    public string EncryptionKeyId { get; set; } = string.Empty;
    public string EncryptionAlgorithm { get; set; } = "AES-256-GCM";
    public DateTime? CompletedAtUtc { get; set; }
    public BackupFileStatus Status { get; set; } = BackupFileStatus.Pending;
    public VerificationStatus VerificationStatus { get; set; } = VerificationStatus.Pending;
    public RetentionClass RetentionClass { get; set; } = RetentionClass.Daily;
    public string? MySqlVersion { get; set; }
    public BackupJob BackupJob { get; set; } = null!;
}

public sealed class BackupRetentionPolicy : AuditableEntity
{
    public int DailyRetentionCount { get; set; } = 7;
    public int MinimumFreeDiskPercentage { get; set; } = 15;
    public bool IsDefault { get; set; } = true;
}

public sealed class EncryptionKeyRecord : Entity
{
    public string KeyId { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "AES-256-GCM";
    public string WrappedKey { get; set; } = string.Empty;
    public string WrapNonce { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RotatedAtUtc { get; set; }
}
