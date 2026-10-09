using System.Text.RegularExpressions;

namespace MySqlManager.Domain.Safety;

public static class SourceSqlGuard
{
    private static readonly HashSet<string> AllowedVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "WITH", "CHECKSUM"
    };

    private static readonly string[] ForbiddenFragments =
    [
        "INTO OUTFILE", "INTO DUMPFILE", "FOR UPDATE", "LOCK TABLES", "LOAD DATA"
    ];

    public static void EnsureReadOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new SourceSafetyException("Empty SQL is not allowed against a source database.");
        }

        var stripped = StripCommentsAndStrings(sql);
        var upper = Regex.Replace(stripped, @"\s+", " ").Trim().ToUpperInvariant();

        foreach (var fragment in ForbiddenFragments)
        {
            if (upper.Contains(fragment, StringComparison.Ordinal))
            {
                throw new SourceSafetyException($"Rejected SQL because it contains '{fragment}'.");
            }
        }

        var statements = upper.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (statements.Length == 0)
        {
            throw new SourceSafetyException("No SQL statement was provided.");
        }

        foreach (var statement in statements)
        {
            var verb = statement.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (!AllowedVerbs.Contains(verb))
            {
                throw new SourceSafetyException($"SQL verb '{verb}' is not allowed against a source database.");
            }
        }
    }

    private static string StripCommentsAndStrings(string sql)
    {
        var withoutBlock = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var withoutLine = Regex.Replace(withoutBlock, @"--.*?$", " ", RegexOptions.Multiline);
        var withoutHash = Regex.Replace(withoutLine, @"#.*?$", " ", RegexOptions.Multiline);
        return Regex.Replace(withoutHash, @"'(?:\\'|[^'])*'", "''");
    }
}

public sealed class SourceSafetyException : Exception
{
    public SourceSafetyException(string message) : base(message)
    {
    }
}
