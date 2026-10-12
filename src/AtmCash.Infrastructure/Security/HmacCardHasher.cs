using System.Security.Cryptography;
using System.Text;
using AtmCash.Application.Abstractions;
using AtmCash.Domain.Common;

namespace AtmCash.Infrastructure.Security;

/// <summary>
/// HMAC-SHA256 with a server-side secret. A plain hash would not be enough: card numbers have so
/// little entropy that every possible number could be hashed and looked up. Without the key the
/// stored values are useless; with it, the same card always maps to the same value for limit checks.
/// </summary>
internal sealed class HmacCardHasher(string secret) : ICardHasher
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret);

    public string Hash(string cardNumber)
    {
        var digits = new string((cardNumber ?? string.Empty).Where(c => c is not (' ' or '-')).ToArray());
        if (digits.Length is < 12 or > 19 || !digits.All(char.IsAsciiDigit))
        {
            throw DomainException.Validation(ErrorCodes.InvalidCardNumber, "Card number must be 12-19 digits.");
        }

        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(digits)));
    }
}
