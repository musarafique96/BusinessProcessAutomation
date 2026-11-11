using System.Text.RegularExpressions;

namespace Bpa.Identity;

/// <summary>
/// Username format. Matches the database check in migration 0003, so the API rejects bad names before the database does.
/// </summary>
public static partial class UsernameRules
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? username) =>
        username is not null && Pattern().IsMatch(username.Trim().ToLowerInvariant());
}
