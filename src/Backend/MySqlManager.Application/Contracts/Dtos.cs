using MySqlManager.Domain.Enums;

namespace MySqlManager.Application.Contracts;

public sealed record AuthResultDto(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAtUtc, UserProfileDto User);
public sealed record UserProfileDto(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);
public sealed record JobCreatedDto(Guid JobId, string Status, string? Detail = null);
public sealed record CopySkipDto(string DatabaseName, string Reason);
public sealed record CopyBatchResultDto(int QueuedCount, Guid? FirstJobId, IReadOnlyList<CopySkipDto> Skipped);
public sealed record ServerDto(
    Guid Id,
    string Name,
    string Description,
    string Host,
    int Port,
    ServerEnvironment Environment,
    bool SslEnabled,
    bool HasSslCertificate,
    int ConnectionTimeoutSeconds,
    bool IsActive,
    string TimeZoneId,
    IReadOnlyList<CredentialDto> Credentials);
public sealed record CredentialDto(Guid Id, CredentialPurpose Purpose, string Username, SecretProviderKind SecretProvider, bool IsPrivilegeValidated, DateTime? LastValidatedAtUtc);
public sealed record ConnectionTestDto(bool Connected, string? MySqlVersion, long? UptimeSeconds, int DatabaseCount, long? AvailableDiskBytes, long? TotalDiskBytes, int ConnectionLatencyMs, bool PrivilegeValidated, string Message);
public sealed record DatabaseDto(Guid Id, Guid ServerId, string ServerName, string Name, long SizeBytes, int TableCount, long EstimatedRows, string Charset, string Collation, bool IsSelectedForBackup, bool IsActive, DateTime? LastDiscoveredAtUtc);
public sealed record BackupConfigurationDto(Guid Id, Guid ServerId, Guid DatabaseId, string DatabaseName, bool IncludeRoutines, bool IncludeTriggers, bool IncludeEvents, bool UseSingleTransaction, bool IsEnabled, int RetentionCount, BackupScheduleDto? Schedule);
public sealed record BackupScheduleDto(Guid Id, string TimeZoneId, ScheduleKind Kind, string? DailyTime, int? IntervalHours, string? CronExpression, bool IsEnabled, DateTime? LastEnqueuedAtUtc);
public sealed record BackupJobDto(Guid Id, Guid ServerId, Guid DatabaseId, string Status, string Trigger, DateTime CreatedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc, string? ErrorMessage, string? ErrorCode, int Attempt, string GitSyncStatus, string? GitSyncError, long? CompressedBytes, double? ProgressPercentage);
public sealed record BackupFileDto(Guid Id, Guid ServerId, Guid DatabaseId, Guid BackupJobId, string FileName, string StoragePath, string BackupType, long SizeBytes, long CompressedSizeBytes, string Checksum, string EncryptionKeyId, string EncryptionAlgorithm, DateTime CreatedAtUtc, DateTime? CompletedAtUtc, string Status, string VerificationStatus, string RetentionClass, string? MySqlVersion, string ServerName = "", string DatabaseName = "");
public sealed record GitRepositoryDto(Guid Id, Guid ServerId, string Name, string RemoteUrl, string Branch, string Mode, string AuthType, bool AllowHistoryRewrite, bool IsEnabled, DateTime? LastPushAtUtc, string? LastCommitSha, string? LastError, bool? LastAuthenticationSucceeded);
public sealed record GitLogDto(string Sha, DateTime CommittedAtUtc, string Subject);
public sealed record MigrationJobDto(Guid Id, Guid SourceServerId, string? SourceServerName, Guid SourceDatabaseId, string? SourceDatabaseName, Guid DestinationServerId, string? DestinationServerName, string DestinationDatabaseName, string Mode, string Status, string CurrentStep, DateTime CreatedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc, long? TotalBytes, long TransferredBytes, double? ProgressPercentage, string? ErrorMessage, string ValidationStatus, string SourceModification, string? ReportJson);
public sealed record RestoreJobDto(Guid Id, Guid BackupFileId, Guid DestinationServerId, string DestinationDatabaseName, string Status, string CurrentStep, DateTime CreatedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc, long? TotalBytes, long TransferredBytes, double? ProgressPercentage, string? ErrorMessage, string ValidationStatus, string SourceModification, string? ReportJson);
public sealed record JobListItemDto(Guid Id, string JobType, string Status, string? ServerName, string? DatabaseName, DateTime CreatedAtUtc, DateTime? CompletedAtUtc, string? ErrorMessage);
public sealed record JobLogDto(Guid Id, Guid JobId, string JobType, string Level, string Message, DateTime CreatedAtUtc);
public sealed record AuditLogDto(Guid Id, Guid? UserId, string Action, string EntityType, string? EntityId, Guid? ServerId, Guid? DatabaseId, Guid? DestinationServerId, string? DestinationDatabase, DateTime Timestamp, string IpAddress, string Result, string Details, string SourceModification);
public sealed record HealthDto(Guid ServerId, string ServerName, bool IsOnline, string? MySqlVersion, long? UptimeSeconds, int? DatabaseCount, int? LatencyMs, long? BackupStorageFreeBytes, long? BackupStorageTotalBytes, double? HostCpuPercent, double? HostMemoryPercent, long? HostDiskFreeBytes, DateTime? LastBackupAtUtc, string? LastBackupStatus, DateTime? LastMigrationAtUtc, DateTime CapturedAtUtc);
public sealed record DashboardDto(int TotalServers, int OnlineServers, int OfflineServers, int Databases, int SuccessfulBackupsToday, int FailedBackupsToday, int BackupsRunning, int MigrationsRunning, long StorageUsedBytes, long StorageFreeBytes, IReadOnlyList<ActivityDto> RecentActivity, IReadOnlyList<ChartPointDto> BackupOutcomes, IReadOnlyList<ChartPointDto> StorageGrowth, IReadOnlyList<ChartPointDto> DatabaseSizes, IReadOnlyList<ChartPointDto> MigrationHistory);
public sealed record ActivityDto(DateTime Timestamp, string Kind, string Message);
public sealed record ChartPointDto(string Label, double Value);
public sealed record CalendarDayDto(DateOnly Date, string Status, Guid? BackupFileId);
public sealed record CalendarServerDto(Guid ServerId, string ServerName, IReadOnlyList<CalendarDayDto> Days);
public sealed record NotificationDto(Guid Id, string Channel, string EventType, string Recipient, string Status, DateTime CreatedAtUtc, DateTime? SentAtUtc, string? Error);
public sealed record NotificationEndpointDto(Guid Id, string Channel, string Name, string Destination, bool IsEnabled, string EventTypesCsv);
public sealed record RoleDto(Guid Id, string Name, string Description, IReadOnlyList<string> Permissions);
public sealed record SettingsDto(int DailyRetentionDays, int MinimumFreeDiskPercentage, bool EncryptionEnabled, bool GitEnabled, bool AllowHistoryRewrite, string StorageType, int MaxConcurrentBackups, int MaxConcurrentMigrations);
