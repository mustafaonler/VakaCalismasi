using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Deposits;

/// <summary>Notes of one currency and denomination recognised by the machine.</summary>
public sealed record DepositNote(Currency Currency, int Denomination, int Count);

public enum ReturnReason
{
    /// <summary>The machine could not recognise the note; it is never part of the transaction.</summary>
    Unrecognised,

    /// <summary>Neither the matching recycling cassette nor the deposit cassette had room.</summary>
    CassetteFull,
}

/// <summary>Notes that went into one cassette.</summary>
public sealed record DepositPlacement(int Slot, CassetteKind CassetteKind, Currency Currency, int Denomination, int Count);

/// <summary>Notes handed back to the customer. Unrecognised notes have no currency or denomination.</summary>
public sealed record ReturnedNotes(ReturnReason Reason, Currency? Currency, int? Denomination, int Count);

public sealed record DepositReceipt(
    Guid DepositId,
    IReadOnlyList<DepositPlacement> Placements,
    IReadOnlyList<ReturnedNotes> Returned);
