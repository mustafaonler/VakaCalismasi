using AtmCash.Domain.Common;

namespace AtmCash.Domain.Reconciliations;

/// <summary>
/// Operators are identified by the X-User header only (no authentication, as the assignment allows).
/// Names are compared case-insensitively so "ayse" cannot approve "Ayse"'s reconciliation.
/// </summary>
public static class Users
{
    public const int MaxLength = 100;

    public static string Require(string? user)
    {
        var trimmed = user?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxLength)
        {
            throw DomainException.Validation(
                ErrorCodes.UserRequired, $"An X-User header of 1-{MaxLength} characters is required.");
        }

        return trimmed;
    }

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
