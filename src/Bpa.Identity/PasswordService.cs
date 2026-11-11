using Microsoft.AspNetCore.Identity;

namespace Bpa.Identity;

/// <summary>
/// Hashes and verifies passwords with ASP.NET Core's PBKDF2 hasher (format v3, 100k+ iterations).
/// Argon2id, named in the data model notes, is a later swap behind this class.
/// </summary>
public sealed class PasswordService
{
    public const int MinimumLength = 12;

    // The hasher doesn't use the subject for anything, so a placeholder object is enough.
    private static readonly object Subject = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (password.Length < MinimumLength)
        {
            throw new ArgumentException($"Password must be at least {MinimumLength} characters.", nameof(password));
        }

        return _hasher.HashPassword(Subject, password);
    }

    public bool Verify(string hash, string password)
    {
        var result = _hasher.VerifyHashedPassword(Subject, hash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
