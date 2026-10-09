using MySqlManager.Domain.Common;
using MySqlManager.Domain.Enums;

namespace MySqlManager.Domain.Servers;

public sealed class Server : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3306;
    public ServerEnvironment Environment { get; set; } = ServerEnvironment.Other;
    public bool SslEnabled { get; set; }
    public string? SslCertificatePem { get; set; }
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public bool IsActive { get; set; } = true;
    public string TimeZoneId { get; set; } = "UTC";
    public ICollection<ServerCredential> Credentials { get; set; } = new List<ServerCredential>();
    public ICollection<ManagedDatabase> Databases { get; set; } = new List<ManagedDatabase>();
    public ICollection<ServerHealthSnapshot> HealthSnapshots { get; set; } = new List<ServerHealthSnapshot>();
}

public sealed class ServerCredential : AuditableEntity
{
    public Guid ServerId { get; set; }
    public CredentialPurpose Purpose { get; set; }
    public string Username { get; set; } = string.Empty;
    public string SecretCiphertext { get; set; } = string.Empty;
    public string SecretNonce { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public SecretProviderKind SecretProvider { get; set; } = SecretProviderKind.LocalEncrypted;
    public string? ExternalSecretName { get; set; }
    public DateTime? LastValidatedAtUtc { get; set; }
    public string GrantsSummary { get; set; } = string.Empty;
    public bool IsPrivilegeValidated { get; set; }
    public Server Server { get; set; } = null!;
}

public sealed class ManagedDatabase : AuditableEntity
{
    public Guid ServerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int TableCount { get; set; }
    public long EstimatedRows { get; set; }
    public string Charset { get; set; } = string.Empty;
    public string Collation { get; set; } = string.Empty;
    public bool IsSelectedForBackup { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastDiscoveredAtUtc { get; set; }
    public Server Server { get; set; } = null!;
}

public sealed class ServerHealthSnapshot : Entity
{
    public Guid ServerId { get; set; }
    public bool IsOnline { get; set; }
    public string? MySqlVersion { get; set; }
    public long? UptimeSeconds { get; set; }
    public int? DatabaseCount { get; set; }
    public int? LatencyMs { get; set; }
    public long? BackupStorageFreeBytes { get; set; }
    public long? BackupStorageTotalBytes { get; set; }
    public double? HostCpuPercent { get; set; }
    public double? HostMemoryPercent { get; set; }
    public long? HostDiskFreeBytes { get; set; }
    public DateTime? LastBackupAtUtc { get; set; }
    public string? LastBackupStatus { get; set; }
    public DateTime? LastMigrationAtUtc { get; set; }
    public string? UnavailableReason { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public Server Server { get; set; } = null!;
}
