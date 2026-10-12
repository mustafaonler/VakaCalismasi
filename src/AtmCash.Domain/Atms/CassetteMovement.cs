using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Atms;

/// <summary>
/// Immutable ledger entry for a single change to one cassette holding (B8).
/// <see cref="Quantity"/> is signed: positive adds notes (or reservations), negative removes them.
/// </summary>
public sealed class CassetteMovement
{
    private CassetteMovement()
    {
    }

    internal CassetteMovement(
        Guid atmId, Guid cassetteId, MovementType type, Currency currency, int denomination, int quantity, Guid? correlationId)
    {
        Id = Guid.CreateVersion7();
        AtmId = atmId;
        CassetteId = cassetteId;
        Type = type;
        Currency = currency;
        Denomination = denomination;
        Quantity = quantity;
        CorrelationId = correlationId;
    }

    public Guid Id { get; private set; }

    public Guid AtmId { get; private set; }

    public Guid CassetteId { get; private set; }

    public MovementType Type { get; private set; }

    public Currency Currency { get; private set; }

    public int Denomination { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>The quote, deposit or reconciliation that caused this movement, if any.</summary>
    public Guid? CorrelationId { get; private set; }

    /// <summary>Set by the persistence layer when the movement is saved.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
