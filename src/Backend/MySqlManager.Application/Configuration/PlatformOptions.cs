using MySqlManager.Domain.Enums;

namespace MySqlManager.Application.Configuration;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "MySqlManager";
    public string Audience { get; set; } = "MySqlManager";
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}

public sealed class BackupOptions
{
    public const string Section = "Backup";
    public string RootPath { get; set; } = "data/backups";
    public string TemporaryPath { get; set; } = "data/tmp";
    public string GitWorkspacePath { get; set; } = "data/git";
    public int DailyRetentionDays { get; set; } = 7;
    public string Compression { get; set; } = "gzip";
    public bool EncryptionEnabled { get; set; } = true;
    public string MasterKeyBase64 { get; set; } = string.Empty;
    public int MinimumFreeDiskPercentage { get; set; } = 15;
    public string MySqlDumpPath { get; set; } = "mysqldump";
    public string MySqlClientPath { get; set; } = "mysql";
    public string GitPath { get; set; } = "git";
}

public sealed class GitOptions
{
    public const string Section = "Git";
    public bool Enabled { get; set; } = true;
    public string DefaultBranch { get; set; } = "main";
    public bool AllowHistoryRewrite { get; set; }
}

public sealed class JobOptions
{
    public const string Section = "Jobs";
    public int MaxConcurrentBackups { get; set; } = 2;
    public int MaxConcurrentMigrations { get; set; } = 1;
    public int PollIntervalSeconds { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 600;
    public int MaxAttempts { get; set; } = 3;
}

public sealed class SeedOptions
{
    public const string Section = "Seed";
    public string AdminEmail { get; set; } = "admin@localhost";
    public string AdminPassword { get; set; } = string.Empty;
    public bool SeedSampleServer { get; set; }
}

public sealed class AllowedCorsOptions
{
    public const string Section = "Cors";
    public string[] Origins { get; set; } = ["http://localhost:5173"];
}

public sealed class NotificationOptions
{
    public const string Section = "Notifications";
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public bool SmtpUseTls { get; set; } = true;
}

public sealed class SecretOptions
{
    public const string Section = "Secrets";
    public SecretProviderKind Provider { get; set; } = SecretProviderKind.LocalEncrypted;
    public string AzureKeyVaultUri { get; set; } = string.Empty;
    public string AwsRegion { get; set; } = string.Empty;
}

public sealed class StorageOptions
{
    public const string Section = "Storage";
    public StorageProviderKind Type { get; set; } = StorageProviderKind.Local;
    public string S3Bucket { get; set; } = string.Empty;
    public string S3Region { get; set; } = string.Empty;
    public string AzureBlobConnectionString { get; set; } = string.Empty;
    public string AzureBlobContainer { get; set; } = "backups";
}

public sealed class RedisOptions
{
    public const string Section = "Redis";
    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
}

public sealed class TelemetryOptions
{
    public const string Section = "OpenTelemetry";
    public string OtlpEndpoint { get; set; } = string.Empty;
}
