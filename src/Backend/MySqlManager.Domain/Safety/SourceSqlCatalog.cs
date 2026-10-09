namespace MySqlManager.Domain.Safety;

public static class SourceSqlCatalog
{
    public const string Version = "SELECT VERSION()";
    public const string Uptime = "SHOW GLOBAL STATUS LIKE 'Uptime'";
    public const string Grants = "SHOW GRANTS";
    public const string Schemas = """
        SELECT SCHEMA_NAME, DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME
        FROM information_schema.SCHEMATA
        """;
    public const string SchemaStats = """
        SELECT TABLE_SCHEMA, COUNT(*) AS TableCount,
               COALESCE(SUM(DATA_LENGTH + INDEX_LENGTH), 0) AS SizeBytes,
               COALESCE(SUM(TABLE_ROWS), 0) AS EstimatedRows
        FROM information_schema.TABLES
        GROUP BY TABLE_SCHEMA
        """;
    public const string ObjectCounts = """
        SELECT
          (SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'BASE TABLE') AS Tables,
          (SELECT COUNT(*) FROM information_schema.VIEWS WHERE TABLE_SCHEMA = @db) AS Views,
          (SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = @db AND ROUTINE_TYPE = 'PROCEDURE') AS Procedures,
          (SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = @db) AS Triggers,
          (SELECT COUNT(*) FROM information_schema.EVENTS WHERE EVENT_SCHEMA = @db) AS Events
        """;
    public const string SchemaMeta = """
        SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME
        FROM information_schema.SCHEMATA
        WHERE SCHEMA_NAME = @db
        """;
    public const string TableNames = """
        SELECT TABLE_NAME
        FROM information_schema.TABLES
        WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'BASE TABLE'
        ORDER BY TABLE_NAME
        """;
    public const string SchemaExists = """
        SELECT SCHEMA_NAME
        FROM information_schema.SCHEMATA
        WHERE SCHEMA_NAME = @db
        """;
    public const string Ping = "SELECT 1";
    public const string Variables = "SHOW VARIABLES LIKE 'version'";

    public static IReadOnlyList<string> All { get; } =
    [
        Version, Uptime, Grants, Schemas, SchemaStats, ObjectCounts, SchemaMeta, TableNames, SchemaExists, Ping, Variables
    ];
}

public static class GitHistoryPolicy
{
    public static bool CanPurge(bool repositoryAllowsRewrite, bool globalAllowsRewrite) =>
        repositoryAllowsRewrite && globalAllowsRewrite;
}

public static class BackupSafetyRules
{
    public static bool IsValidRestorePoint(
        Enums.BackupFileStatus status,
        Enums.VerificationStatus verification) =>
        status == Enums.BackupFileStatus.Completed &&
        verification == Enums.VerificationStatus.Passed;

    public static bool MayDeleteOlderBackups(
        Enums.BackupJobStatus jobStatus,
        Enums.VerificationStatus verification) =>
        jobStatus == Enums.BackupJobStatus.Completed &&
        verification == Enums.VerificationStatus.Passed;
}
