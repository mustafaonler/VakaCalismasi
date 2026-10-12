namespace AtmCash.Application.Abstractions;

/// <summary>Turns a card number into a stable, non-reversible key; card numbers are never stored (B5).</summary>
public interface ICardHasher
{
    /// <exception cref="Domain.Common.DomainException">The card number is not 12-19 digits.</exception>
    string Hash(string cardNumber);
}
