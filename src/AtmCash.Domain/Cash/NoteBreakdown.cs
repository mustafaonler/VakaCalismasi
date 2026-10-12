namespace AtmCash.Domain.Cash;

public readonly record struct NoteLine(int Denomination, int Count);

/// <summary>A payable amount and the notes that make it up, largest denomination first.</summary>
public sealed record NoteBreakdown(int Amount, IReadOnlyList<NoteLine> Notes)
{
    public int NoteCount => Notes.Sum(n => n.Count);
}

/// <summary>
/// Result of note selection: either an exact breakdown, or the closest
/// payable alternatives below and above the requested amount.
/// </summary>
public sealed record WithdrawalOptions(NoteBreakdown? Exact, NoteBreakdown? Lower, NoteBreakdown? Upper)
{
    public bool IsExact => Exact is not null;
}
