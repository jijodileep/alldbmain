using MySqlManager.Domain.Backups;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Git;
using MySqlManager.Domain.Identity;
using MySqlManager.Domain.Migrations;
using MySqlManager.Domain.Operations;
using MySqlManager.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace MySqlManager.Application.Abstractions;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Server> Servers { get; }
    DbSet<ServerCredential> ServerCredentials { get; }
    DbSet<ManagedDatabase> Databases { get; }
    DbSet<ServerHealthSnapshot> ServerHealthSnapshots { get; }
    DbSet<BackupConfiguration> BackupConfigurations { get; }
    DbSet<BackupSchedule> BackupSchedules { get; }
    DbSet<BackupJob> BackupJobs { get; }
    DbSet<BackupFile> BackupFiles { get; }
    DbSet<BackupRetentionPolicy> BackupRetentionPolicies { get; }
    DbSet<EncryptionKeyRecord> EncryptionKeys { get; }
    DbSet<MigrationJob> MigrationJobs { get; }
    DbSet<MigrationValidation> MigrationValidations { get; }
    DbSet<RestoreJob> RestoreJobs { get; }
    DbSet<GitRepository> GitRepositories { get; }
    DbSet<GitBackupRecord> GitBackupRecords { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<NotificationEndpoint> NotificationEndpoints { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<JobLog> JobLogs { get; }
    DbSet<JobProgressEvent> JobProgressEvents { get; }
    DbSet<DistributedLockRecord> DistributedLocks { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public interface ICurrentUser
{
    Guid? UserId { get; }
    string IpAddress { get; }
}

public interface IPasswordHasher
{
    string Hash(User user, string password);
    bool Verify(User user, string passwordHash, string password);
}

public interface ITokenService
{
    (string AccessToken, DateTime ExpiresAtUtc) CreateAccessToken(User user, IReadOnlyList<string> roles, IReadOnlyList<string> permissions);
}

public interface IAuditWriter
{
    Task WriteAsync(AuditWriteRequest request, CancellationToken cancellationToken);
}

public sealed record AuditWriteRequest(
    string Action,
    string EntityType,
    string? EntityId,
    Guid? ServerId,
    Guid? DatabaseId,
    Guid? DestinationServerId,
    string? DestinationDatabase,
    Domain.Enums.AuditResult Result,
    string Details,
    string SourceModification = "NONE");

public interface ISecretProtector
{
    Task<ProtectedSecret> ProtectAsync(string plaintext, SecretProviderKind provider, string? externalName, CancellationToken cancellationToken);
    Task<string> UnprotectAsync(ProtectedSecret secret, SecretProviderKind provider, CancellationToken cancellationToken);
}

public sealed record ProtectedSecret(string Ciphertext, string Nonce, string KeyId, string? ExternalName);

public sealed record MySqlEndpoint(
    string Host,
    int Port,
    string Username,
    string Password,
    bool SslEnabled,
    string? SslCertificatePem,
    int TimeoutSeconds,
    ConnectionPurpose Purpose);

public sealed record MySqlServerFacts(
    bool Connected,
    string? MySqlVersion,
    long? UptimeSeconds,
    int DatabaseCount,
    int LatencyMs,
    long? BackupStorageFreeBytes,
    long? BackupStorageTotalBytes,
    string? Error);

public sealed record DiscoveredSchema(
    string Name,
    long SizeBytes,
    int TableCount,
    long EstimatedRows,
    string Charset,
    string Collation);

public sealed record SchemaInventory(
    bool Exists,
    string Charset,
    string Collation,
    int Tables,
    int Views,
    int Procedures,
    int Triggers,
    int Events,
    long EstimatedRows,
    long SizeBytes,
    IReadOnlyList<TableChecksumSample> Checksums);

public sealed record TableChecksumSample(string Table, long? Checksum);

public interface ISourceMySqlGateway
{
    Task<MySqlServerFacts> GetFactsAsync(MySqlEndpoint endpoint, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetGrantsAsync(MySqlEndpoint endpoint, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiscoveredSchema>> DiscoverAsync(MySqlEndpoint endpoint, CancellationToken cancellationToken);
    Task<SchemaInventory> GetInventoryAsync(MySqlEndpoint endpoint, string databaseName, bool includeChecksumSample, CancellationToken cancellationToken);
}

public interface IDestinationMySqlGateway
{
    Task<MySqlServerFacts> GetFactsAsync(MySqlEndpoint endpoint, CancellationToken cancellationToken);
    Task<bool> DatabaseExistsAsync(MySqlEndpoint endpoint, string databaseName, CancellationToken cancellationToken);
    Task CreateDatabaseAsync(MySqlEndpoint endpoint, string databaseName, string charset, string collation, CancellationToken cancellationToken);
    Task<SchemaInventory> GetInventoryAsync(MySqlEndpoint endpoint, string databaseName, bool includeChecksumSample, CancellationToken cancellationToken);
}

public sealed record DumpRequest(
    MySqlEndpoint Endpoint,
    string DatabaseName,
    bool SingleTransaction,
    bool Routines,
    bool Triggers,
    bool Events,
    string GzipOutputPath);

public sealed record DumpResult(long UncompressedBytes, long GzipBytes, string? MySqlVersion);

public interface IMySqlBackupService
{
    Task<DumpResult> DumpAsync(DumpRequest request, CancellationToken cancellationToken);
}

public interface IMySqlImportService
{
    Task ImportAsync(MySqlEndpoint endpoint, string databaseName, Stream sqlStream, IProgress<long>? progress, CancellationToken cancellationToken);
}

public interface IEncryptionService
{
    Task<string> EncryptFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
    Stream OpenDecrypt(string encryptedPath, string keyId);
    Task VerifyAsync(string encryptedPath, string keyId, string expectedSha256, CancellationToken cancellationToken);
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken);
}

public interface ICompressionService
{
    Stream OpenDecompress(Stream gzipStream);
}

public sealed record DiskUsage(long FreeBytes, long TotalBytes, double FreePercentage);

public interface IBackupStorage
{
    string RootPath { get; }
    DiskUsage GetDiskUsage();
    string Promote(string temporaryFile, string relativeDirectory, string fileName);
    Stream OpenRead(string storagePath);
    void Delete(string storagePath);
}

public sealed record GitSyncWorkItem(
    Guid RepositoryId,
    string RemoteUrl,
    string Branch,
    GitMode Mode,
    GitAuthType AuthType,
    string? Secret,
    bool AllowHistoryRewrite,
    string ServerFolder,
    string DatabaseFolder,
    string? BackupFilePath,
    string? BackupFileName,
    string ManifestJson,
    IReadOnlyList<string> RetainedFileNames);

public sealed record GitConnectionReport(
    string Repository,
    string Branch,
    bool Authenticated,
    DateTime? LastPushAtUtc,
    string? LastCommitSha,
    string? Error);

public sealed record GitLogEntry(string Sha, DateTime CommittedAtUtc, string Subject);

public interface IGitService
{
    Task<GitConnectionReport> TestAsync(GitSyncWorkItem item, CancellationToken cancellationToken);
    Task<string?> SyncAsync(GitSyncWorkItem item, CancellationToken cancellationToken);
    Task PurgeHistoryAsync(GitSyncWorkItem item, CancellationToken cancellationToken);
    Task<IReadOnlyList<GitLogEntry>> GetLogAsync(GitSyncWorkItem item, CancellationToken cancellationToken);
}

public sealed record ProgressUpdate(
    Guid JobId,
    JobKind JobType,
    string Step,
    string State,
    double? Percentage,
    string Message);

public interface IJobProgressPublisher
{
    Task PublishAsync(ProgressUpdate update, CancellationToken cancellationToken);
}

public interface INotificationDispatcher
{
    Task EnqueueAsync(string eventType, string summary, CancellationToken cancellationToken);
    Task DispatchPendingAsync(CancellationToken cancellationToken);
}

public interface IDistributedLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken);
}

public interface IJobQueue
{
    Task<Guid?> TryLeaseBackupAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);
    Task<Guid?> TryLeaseMigrationAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);
    Task<Guid?> TryLeaseRestoreAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);
}

public interface IBackupPipeline
{
    Task ExecuteAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface ICopyPipeline
{
    Task ExecuteAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface IRestorePipeline
{
    Task ExecuteAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface IHealthProbe
{
    Task ProbeAllAsync(CancellationToken cancellationToken);
}

public interface IRetentionService
{
    Task ApplyAsync(Guid serverId, Guid databaseId, int keepCount, CancellationToken cancellationToken);
}

public interface IBackupScheduleEnqueuer
{
    Task EnqueueDueAsync(CancellationToken cancellationToken);
}
