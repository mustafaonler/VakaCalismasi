using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;

namespace AtmCash.Domain.Atms;

/// <summary>
/// One of the five physical cassettes of an ATM. Every change goes through a method that
/// returns the matching <see cref="CassetteMovement"/>, so the ledger cannot be bypassed.
/// </summary>
public sealed class Cassette
{
    public const int Capacity = 100;

    /// <summary>A recycling cassette with fewer available notes than this (10% of capacity) is low on cash.</summary>
    public const int LowCashThreshold = Capacity / 10;

    private readonly List<CassetteHolding> _holdings = [];

    private Cassette()
    {
    }

    internal Cassette(Guid atmId, int slot, CassetteKind kind, Currency? currency, int? denomination)
    {
        Id = Guid.CreateVersion7();
        AtmId = atmId;
        Slot = slot;
        Kind = kind;
        Currency = currency;
        Denomination = denomination;
    }

    public Guid Id { get; private set; }

    public Guid AtmId { get; private set; }

    /// <summary>1-4 are recycling cassettes, 5 is the deposit cassette.</summary>
    public int Slot { get; private set; }

    public CassetteKind Kind { get; private set; }

    /// <summary>Configured currency of a recycling cassette; <c>null</c> for the deposit cassette.</summary>
    public Currency? Currency { get; private set; }

    /// <summary>Configured denomination of a recycling cassette; <c>null</c> for the deposit cassette.</summary>
    public int? Denomination { get; private set; }

    public IReadOnlyList<CassetteHolding> Holdings => _holdings;

    public int Count => _holdings.Sum(h => h.Count);

    public int Reserved => _holdings.Sum(h => h.Reserved);

    public int Available => Count - Reserved;

    public int FreeSpace => Capacity - Count;

    public bool IsEmpty => Count == 0;

    public bool IsLowOnCash => Kind == CassetteKind.Recycling && Available < LowCashThreshold;

    public bool IsConfiguredFor(Currency currency, int denomination) =>
        Kind == CassetteKind.Recycling && Currency == currency && Denomination == denomination;

    public bool Accepts(Currency currency, int denomination) =>
        Kind == CassetteKind.Deposit || IsConfiguredFor(currency, denomination);

    internal void Reconfigure(Currency currency, int denomination)
    {
        if (!IsEmpty)
        {
            throw DomainException.Conflict(
                ErrorCodes.CassetteNotEmpty,
                $"Cassette {Slot} still holds {Count} notes; unload it before changing its configuration.");
        }

        Currency = currency;
        Denomination = denomination;
        _holdings.Clear();
    }

    internal CassetteMovement Add(MovementType type, Currency currency, int denomination, int quantity, Guid? correlationId = null)
    {
        EnsurePositive(quantity);
        if (!Accepts(currency, denomination))
        {
            throw DomainException.Validation(
                ErrorCodes.InvalidCassetteConfiguration,
                $"Cassette {Slot} does not accept {currency} {denomination} notes.");
        }

        if (quantity > FreeSpace)
        {
            throw DomainException.Conflict(
                ErrorCodes.CassetteCapacityExceeded,
                $"Cassette {Slot} has room for {FreeSpace} more notes, cannot add {quantity}.");
        }

        GetOrCreateHolding(currency, denomination).Count += quantity;
        return Movement(type, currency, denomination, quantity, correlationId);
    }

    /// <summary>Removes notes that are not reserved (e.g. unloading).</summary>
    internal CassetteMovement Remove(MovementType type, Currency currency, int denomination, int quantity, Guid? correlationId = null)
    {
        EnsurePositive(quantity);
        var holding = FindHolding(currency, denomination);
        if (holding is null || holding.Available < quantity)
        {
            throw DomainException.Conflict(
                ErrorCodes.InsufficientNotes,
                $"Cassette {Slot} has {holding?.Available ?? 0} available {currency} {denomination} notes, cannot remove {quantity}.");
        }

        holding.Count -= quantity;
        return Movement(type, currency, denomination, -quantity, correlationId);
    }

    /// <summary>Earmarks notes of a recycling cassette for a withdrawal quote.</summary>
    internal CassetteMovement Reserve(int quantity, Guid quoteId)
    {
        EnsurePositive(quantity);
        var holding = ConfiguredHolding();
        if (holding is null || holding.Available < quantity)
        {
            throw DomainException.Conflict(
                ErrorCodes.InsufficientNotes,
                $"Cassette {Slot} has {holding?.Available ?? 0} available notes, cannot reserve {quantity}.");
        }

        holding.Reserved += quantity;
        return Movement(MovementType.Reservation, holding.Currency, holding.Denomination, quantity, quoteId);
    }

    internal CassetteMovement ReleaseReservation(int quantity, Guid quoteId)
    {
        EnsurePositive(quantity);
        var holding = ConfiguredHolding();
        if (holding is null || holding.Reserved < quantity)
        {
            throw new InvalidOperationException($"Cassette {Slot} has fewer than {quantity} reserved notes.");
        }

        holding.Reserved -= quantity;
        return Movement(MovementType.ReservationRelease, holding.Currency, holding.Denomination, -quantity, quoteId);
    }

    /// <summary>Pays out reserved notes: the reservation is released and the notes leave the cassette.</summary>
    internal IReadOnlyList<CassetteMovement> Dispense(int quantity, Guid quoteId)
    {
        var release = ReleaseReservation(quantity, quoteId);
        var holding = ConfiguredHolding()!;
        holding.Count -= quantity;
        return [release, Movement(MovementType.Withdrawal, holding.Currency, holding.Denomination, -quantity, quoteId)];
    }

    /// <summary>Removes every unreserved note from the cassette, one movement per holding.</summary>
    internal List<CassetteMovement> RemoveAll(MovementType type, Guid? correlationId = null) =>
        _holdings
            .Where(h => h.Available > 0)
            .ToList()
            .Select(h => Remove(type, h.Currency, h.Denomination, h.Available, correlationId))
            .ToList();

    private CassetteHolding GetOrCreateHolding(Currency currency, int denomination)
    {
        var holding = FindHolding(currency, denomination);
        if (holding is null)
        {
            holding = new CassetteHolding(currency, denomination);
            _holdings.Add(holding);
        }

        return holding;
    }

    private CassetteHolding? ConfiguredHolding() =>
        Kind == CassetteKind.Recycling ? FindHolding(Currency!.Value, Denomination!.Value) : null;

    private CassetteHolding? FindHolding(Currency currency, int denomination) =>
        _holdings.SingleOrDefault(h => h.Currency == currency && h.Denomination == denomination);

    private CassetteMovement Movement(MovementType type, Currency currency, int denomination, int quantity, Guid? correlationId) =>
        new(AtmId, Id, type, currency, denomination, quantity, correlationId);

    private static void EnsurePositive(int quantity)
    {
        if (quantity <= 0)
        {
            throw DomainException.Validation(ErrorCodes.InvalidQuantity, "Quantity must be greater than zero.");
        }
    }
}
