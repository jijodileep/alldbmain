using MySqlManager.Domain.Common;
using MySqlManager.Domain.Enums;

namespace MySqlManager.Domain.Operations;

public sealed class Notification : Entity
{
    public NotificationChannel Channel { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? Error { get; set; }
    public int Attempt { get; set; }
}

public sealed class NotificationEndpoint : AuditableEntity
{
    public NotificationChannel Channel { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string EventTypesCsv { get; set; } = string.Empty;
}

public sealed class AuditLog : Entity
{
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public Guid? ServerId { get; set; }
    public Guid? DatabaseId { get; set; }
    public Guid? DestinationServerId { get; set; }
    public string? DestinationDatabase { get; set; }
    public DateTime Timestamp { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public AuditResult Result { get; set; }
    public string Details { get; set; } = string.Empty;
    public string SourceModification { get; set; } = "NONE";
}

public sealed class JobLog : Entity
{
    public Guid JobId { get; set; }
    public JobKind JobType { get; set; }
    public string Level { get; set; } = "Information";
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class JobProgressEvent : Entity
{
    public Guid JobId { get; set; }
    public JobKind JobType { get; set; }
    public string Step { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public double? Percentage { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool Delivered { get; set; }
}

public sealed class DistributedLockRecord
{
    public string LockName { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}
