using System.Text.RegularExpressions;

namespace MySqlManager.Domain.Safety;

public static class DatabaseNameRules
{
    private static readonly Regex SafeName = new("^[A-Za-z0-9_]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex SafeDatabaseName = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "mysql", "information_schema", "performance_schema", "sys"
    };

    public static bool IsSafeIdentifier(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SafeName.IsMatch(name);

    public static bool IsSafeDatabaseName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SafeDatabaseName.IsMatch(name);

    public static bool IsSystemSchema(string name) => SystemSchemas.Contains(name);

    public static string RequireSafe(string name)
    {
        if (!IsSafeDatabaseName(name))
        {
            throw new SourceSafetyException("Database names may contain only letters, numbers, underscores, and hyphens.");
        }

        return name;
    }

    public static string Quote(string name) => $"`{RequireSafe(name)}`";
}

public static class BackupPathRules
{
    public static string SanitizeSegment(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\') ||
            trimmed.Contains(':') ||
            Path.IsPathRooted(trimmed))
        {
            throw new SourceSafetyException("Path segments cannot contain traversal or absolute paths.");
        }

        var cleaned = Regex.Replace(trimmed, @"[^A-Za-z0-9_\-]", "_");
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new SourceSafetyException("Path segment is empty after sanitization.");
        }

        return cleaned;
    }

    public static string BuildRelativeDirectory(string serverName) => SanitizeSegment(serverName);

    public static string BuildFileName(string databaseName, DateTime utcTimestamp) =>
        $"{SanitizeSegment(databaseName)}_{utcTimestamp:yyyy-MM-dd_HHmmss}.sql.gz.enc";

    public static string EnsureInsideRoot(string rootPath, string candidatePath)
    {
        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(candidatePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison) &&
            !string.Equals(full, root, comparison))
        {
            throw new SourceSafetyException("Resolved path escapes the configured storage root.");
        }

        return full;
    }
}
