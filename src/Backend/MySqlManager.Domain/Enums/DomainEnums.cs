namespace MySqlManager.Domain.Enums;

public enum ServerEnvironment
{
    Production = 1,
    Staging = 2,
    Development = 3,
    Testing = 4,
    Other = 5
}

public enum CredentialPurpose
{
    BackupReadOnly = 1,
    DestinationWrite = 2
}

public enum ConnectionPurpose
{
    Management = 1,
    BackupReadOnly = 2,
    DestinationWrite = 3
}

public enum SecretProviderKind
{
    LocalEncrypted = 1,
    AzureKeyVault = 2,
    AwsSecretsManager = 3,
    Environment = 4
}

public enum BackupType
{
    Full = 1
}

public enum BackupJobStatus
{
    Pending = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}

public enum BackupFileStatus
{
    Pending = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Deleted = 5
}

public enum VerificationStatus
{
    Pending = 1,
    Passed = 2,
    Failed = 3
}

public enum RetentionClass
{
    Daily = 1
}

public enum ScheduleKind
{
    DailyTime = 1,
    Interval = 2,
    Cron = 3
}

public enum JobTriggerKind
{
    Manual = 1,
    Schedule = 2
}

public enum GitMode
{
    MetadataOnly = 1,
    BackupFilesWithLfs = 2,
    RollingRepository = 3
}

public enum GitAuthType
{
    HttpsPat = 1,
    SshDeployKey = 2
}

public enum GitSyncStatus
{
    NotRequested = 1,
    Pending = 2,
    Succeeded = 3,
    Failed = 4
}

public enum CopyMode
{
    Copy = 1,
    Clone = 2
}

public enum PipelineJobStatus
{
    Pending = 1,
    Preflight = 2,
    Dumping = 3,
    Compressing = 4,
    Transferring = 5,
    CreatingDatabase = 6,
    Importing = 7,
    Validating = 8,
    Completed = 9,
    Failed = 10,
    Cancelled = 11
}

public enum ValidationLevel
{
    Basic = 1,
    Standard = 2,
    Deep = 3
}

public enum JobKind
{
    Backup = 1,
    Restore = 2,
    Migration = 3,
    Clone = 4,
    GitSync = 5,
    Cleanup = 6,
    HealthCheck = 7
}

public enum NotificationChannel
{
    Email = 1,
    Webhook = 2
}

public enum NotificationStatus
{
    Pending = 1,
    Sent = 2,
    Failed = 3
}

public enum AuditResult
{
    Success = 1,
    Failure = 2
}

public enum StorageProviderKind
{
    Local = 1,
    S3 = 2,
    AzureBlob = 3
}

public enum PermanentErrorCode
{
    None = 0,
    Validation = 1,
    InsufficientDiskSpace = 2,
    PrivilegeFailure = 3,
    DestinationAlreadyExists = 4,
    VersionMismatch = 5,
    ChecksumMismatch = 6,
    SafetyViolation = 7,
    ToolMissing = 8
}
