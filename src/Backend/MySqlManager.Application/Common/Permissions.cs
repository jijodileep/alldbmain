namespace MySqlManager.Application.Common;

public static class Permissions
{
    public const string ServerView = "Server.View";
    public const string ServerCreate = "Server.Create";
    public const string ServerEdit = "Server.Edit";
    public const string ServerDelete = "Server.Delete";
    public const string BackupView = "Backup.View";
    public const string BackupCreate = "Backup.Create";
    public const string BackupDelete = "Backup.Delete";
    public const string BackupRestore = "Backup.Restore";
    public const string MigrationView = "Migration.View";
    public const string MigrationCreate = "Migration.Create";
    public const string MigrationCancel = "Migration.Cancel";
    public const string GitView = "Git.View";
    public const string GitConfigure = "Git.Configure";
    public const string GitPurge = "Git.Purge";
    public const string AuditView = "Audit.View";
    public const string UserManage = "User.Manage";

    public static IReadOnlyList<(string Code, string Description)> All { get; } =
    [
        (ServerView, "View servers"),
        (ServerCreate, "Register servers"),
        (ServerEdit, "Edit servers"),
        (ServerDelete, "Delete server registrations"),
        (BackupView, "View backups"),
        (BackupCreate, "Create backups and schedules"),
        (BackupDelete, "Delete backup files"),
        (BackupRestore, "Restore backups to a new database"),
        (MigrationView, "View copy and clone jobs"),
        (MigrationCreate, "Start copy and clone jobs"),
        (MigrationCancel, "Cancel copy and clone jobs"),
        (GitView, "View Git repositories"),
        (GitConfigure, "Configure Git repositories"),
        (GitPurge, "Purge Git history"),
        (AuditView, "View audit logs"),
        (UserManage, "Manage users and roles")
    ];

    public static IReadOnlyList<string> ForRole(string roleName) => roleName switch
    {
        "SuperAdmin" => All.Select(p => p.Code).ToList(),
        "Administrator" => All.Select(p => p.Code).ToList(),
        "Operator" =>
        [
            ServerView, BackupView, BackupCreate, BackupRestore,
            MigrationView, MigrationCreate, MigrationCancel, GitView
        ],
        "Viewer" =>
        [
            ServerView, BackupView, MigrationView, GitView, AuditView
        ],
        _ => []
    };
}

public static class AuditActions
{
    public const string ServerCreated = "SERVER_CREATED";
    public const string ServerUpdated = "SERVER_UPDATED";
    public const string ServerDeleted = "SERVER_DELETED";
    public const string BackupStarted = "BACKUP_STARTED";
    public const string BackupCompleted = "BACKUP_COMPLETED";
    public const string BackupFailed = "BACKUP_FAILED";
    public const string BackupDeleted = "BACKUP_DELETED";
    public const string MigrationStarted = "MIGRATION_STARTED";
    public const string MigrationCompleted = "MIGRATION_COMPLETED";
    public const string MigrationFailed = "MIGRATION_FAILED";
    public const string RestoreStarted = "RESTORE_STARTED";
    public const string RestoreCompleted = "RESTORE_COMPLETED";
    public const string RestoreFailed = "RESTORE_FAILED";
    public const string GitPush = "GIT_PUSH";
    public const string GitPurge = "GIT_PURGE";
    public const string UserCreated = "USER_CREATED";
    public const string JobCancelled = "JOB_CANCELLED";
}

public static class NotificationEvents
{
    public const string BackupSuccess = "BACKUP_SUCCESS";
    public const string BackupFailure = "BACKUP_FAILURE";
    public const string MigrationSuccess = "MIGRATION_SUCCESS";
    public const string MigrationFailure = "MIGRATION_FAILURE";
    public const string RestoreSuccess = "RESTORE_SUCCESS";
    public const string RestoreFailure = "RESTORE_FAILURE";
    public const string LowDiskSpace = "LOW_DISK_SPACE";
    public const string ServerUnavailable = "SERVER_UNAVAILABLE";
    public const string GitPushFailure = "GIT_PUSH_FAILURE";

    public static IReadOnlyList<string> All { get; } =
    [
        BackupSuccess, BackupFailure, MigrationSuccess, MigrationFailure,
        RestoreSuccess, RestoreFailure, LowDiskSpace, ServerUnavailable, GitPushFailure
    ];
}
