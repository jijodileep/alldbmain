using System.Text.RegularExpressions;

namespace MySqlManager.Domain.Safety;

public static class MySqlGrantAnalyzer
{
    private static readonly string[] ForbiddenPrivileges =
    [
        "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP", "TRUNCATE",
        "ALL PRIVILEGES", "ALL", "SUPER", "GRANT OPTION", "INDEX", "REFERENCES"
    ];

    public static GrantAnalysis Analyze(IEnumerable<string> grantRows, bool requireReadOnly)
    {
        var rows = grantRows.Where(static row => !string.IsNullOrWhiteSpace(row)).ToList();
        if (rows.Count == 0)
        {
            return new GrantAnalysis(false, "No GRANT rows were returned. The credential is rejected.");
        }

        var combined = string.Join('\n', rows);
        var privileges = ExtractPrivileges(combined);
        if (!requireReadOnly)
        {
            return new GrantAnalysis(true, combined);
        }

        foreach (var privilege in privileges)
        {
            if (ForbiddenPrivileges.Any(forbidden =>
                    privilege.Equals(forbidden, StringComparison.OrdinalIgnoreCase) ||
                    privilege.StartsWith(forbidden + " ", StringComparison.OrdinalIgnoreCase)))
            {
                return new GrantAnalysis(false, $"Source credential includes '{privilege}', which can modify data. Use a dedicated backup account.");
            }
        }

        if (!privileges.Contains("SELECT") && !privileges.Contains("ALL"))
        {
            return new GrantAnalysis(false, "Source credential is missing SELECT, which is required for read-only backup.");
        }

        return new GrantAnalysis(true, combined);
    }

    private static HashSet<string> ExtractPrivileges(string grants)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(grants, @"GRANT\s+(.+?)\s+ON\s+", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var list = match.Groups[1].Value;
            foreach (var part in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var privilege = part.Trim();
                if (privilege.Contains('('))
                {
                    privilege = privilege[..privilege.IndexOf('(')].Trim();
                }

                set.Add(privilege);
            }
        }

        if (Regex.IsMatch(grants, @"ALL\s+PRIVILEGES", RegexOptions.IgnoreCase))
        {
            set.Add("ALL PRIVILEGES");
        }

        return set;
    }
}

public sealed record GrantAnalysis(bool IsAcceptable, string Summary);
