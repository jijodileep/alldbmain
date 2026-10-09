using MySqlManager.Domain.Common;
using MySqlManager.Domain.Enums;

namespace MySqlManager.Domain.Migrations;

public sealed class MigrationJob : AuditableEntity
{
    public Guid SourceServerId { get; set; }
    public Guid SourceDatabaseId { get; set; }
    public Guid DestinationServerId { get; set; }
    public string DestinationDatabaseName { get; set; } = string.Empty;
    public CopyMode Mode { get; set; } = CopyMode.Copy;
    public PipelineJobStatus Status { get; set; } = PipelineJobStatus.Pending;
    public ValidationLevel ValidationLevel { get; set; } = ValidationLevel.Standard;
    public bool AcknowledgeVersionMismatch { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long? TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public double? ProgressPercentage { get; set; }
    public string CurrentStep { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public PermanentErrorCode ErrorCode { get; set; } = PermanentErrorCode.None;
    public VerificationStatus ValidationStatus { get; set; } = VerificationStatus.Pending;
    public string? ReportJson { get; set; }
    public string SourceModification { get; set; } = "NONE";
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public bool CancellationRequested { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public ICollection<MigrationValidation> Validations { get; set; } = new List<MigrationValidation>();
}

public sealed class MigrationValidation : Entity
{
    public Guid MigrationJobId { get; set; }
    public string CheckName { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Details { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; }
    public MigrationJob MigrationJob { get; set; } = null!;
}

public sealed class RestoreJob : AuditableEntity
{
    public Guid BackupFileId { get; set; }
    public Guid DestinationServerId { get; set; }
    public string DestinationDatabaseName { get; set; } = string.Empty;
    public PipelineJobStatus Status { get; set; } = PipelineJobStatus.Pending;
    public ValidationLevel ValidationLevel { get; set; } = ValidationLevel.Standard;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long? TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public double? ProgressPercentage { get; set; }
    public string CurrentStep { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public PermanentErrorCode ErrorCode { get; set; } = PermanentErrorCode.None;
    public VerificationStatus ValidationStatus { get; set; } = VerificationStatus.Pending;
    public string? ReportJson { get; set; }
    public string SourceModification { get; set; } = "NONE";
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public bool CancellationRequested { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public bool ConfirmationAccepted { get; set; }
}
