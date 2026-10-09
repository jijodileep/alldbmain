using System.Text.RegularExpressions;

namespace MySqlManager.Application.Common;

public static partial class SecretRedactor
{
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return CredentialPattern().Replace(value, "$1=***");
    }

    [GeneratedRegex(@"(?i)(password|pwd|secret|token|authorization)\s*[=:]\s*\S+", RegexOptions.Compiled)]
    private static partial Regex CredentialPattern();
}
