using MySqlManager.Domain.Common;
using MySqlManager.Domain.Enums;

namespace MySqlManager.Domain.Git;

public sealed class GitRepository : AuditableEntity
{
    public Guid ServerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public GitMode Mode { get; set; } = GitMode.BackupFilesWithLfs;
    public GitAuthType AuthType { get; set; } = GitAuthType.HttpsPat;
    public string? SecretCiphertext { get; set; }
    public string? SecretNonce { get; set; }
    public string? KeyId { get; set; }
    public SecretProviderKind SecretProvider { get; set; } = SecretProviderKind.LocalEncrypted;
    public string? ExternalSecretName { get; set; }
    public bool AllowHistoryRewrite { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastPushAtUtc { get; set; }
    public string? LastCommitSha { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastTestedAtUtc { get; set; }
    public bool? LastAuthenticationSucceeded { get; set; }
    public ICollection<GitBackupRecord> Records { get; set; } = new List<GitBackupRecord>();
}

public sealed class GitBackupRecord : Entity
{
    public Guid GitRepositoryId { get; set; }
    public Guid? BackupFileId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? CommitSha { get; set; }
    public GitSyncStatus Status { get; set; } = GitSyncStatus.Pending;
    public DateTime? PushedAtUtc { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public GitRepository Repository { get; set; } = null!;
}
