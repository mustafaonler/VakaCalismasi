using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;

namespace AtmCash.Domain.Reconciliations;

public enum ReconciliationStatus
{
    /// <summary>Differences were small enough to be accepted automatically.</summary>
    Completed,

    /// <summary>Differences exceed the threshold; a second user must approve before the ATM returns to service.</summary>
    AwaitingApproval,

    Approved,
}

/// <summary>Counted notes of one currency and denomination.</summary>
public sealed record CountedNote(Currency Currency, int Denomination, int Count);

/// <summary>
/// The physical count of one cassette: <see cref="Count"/> for a recycling cassette (its currency and
/// denomination are known), <see cref="Notes"/> for the mixed deposit cassette (an empty list means empty).
/// </summary>
public sealed record CassetteCount(int Slot, int? Count, IReadOnlyList<CountedNote>? Notes);

/// <summary>System figure and physical count of one cassette holding.</summary>
public sealed record CountComparison(int Slot, Currency Currency, int Denomination, int Expected, int Counted);

/// <summary>System figure versus physical count for one cassette holding.</summary>
public sealed record ReconciliationLine(
    int Slot, Currency Currency, int Denomination, int Expected, int Counted, decimal DifferenceTry)
{
    public int Difference => Counted - Expected;
}

/// <summary>
/// A manual count of an ATM's cassettes (Part A #8, B9). The differences are booked immediately as
/// RECONCILIATION_ADJUSTMENT movements so the system matches the physical cash; when their TRY value
/// exceeds the threshold the ATM may not return to service until a different user approves.
/// </summary>
/// <remarks>
/// The threshold applies to the sum of absolute TRY values: a shortage in one cassette and an
/// equal surplus in another must not cancel each other out.
/// </remarks>
public sealed class Reconciliation
{
    public const decimal ApprovalThresholdTry = 1_000m;

    private readonly List<ReconciliationLine> _lines = [];

    private Reconciliation()
    {
        PerformedBy = null!;
    }

    private Reconciliation(Guid atmId, string performedBy, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        AtmId = atmId;
        PerformedBy = performedBy;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }

    public Guid AtmId { get; private set; }

    public ReconciliationStatus Status { get; private set; }

    public string PerformedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>Sum of the absolute TRY value of every difference.</summary>
    public decimal TotalDifferenceTry { get; private set; }

    public IReadOnlyList<ReconciliationLine> Lines => _lines;

    /// <param name="ratesToTry">TRY value of one unit of each currency.</param>
    public static Reconciliation Perform(
        Atm atm,
        string performedBy,
        IReadOnlyList<CassetteCount> counts,
        IReadOnlyDictionary<Currency, decimal> ratesToTry,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(atm);
        ArgumentNullException.ThrowIfNull(ratesToTry);
        var user = Users.Require(performedBy);

        var reconciliation = new Reconciliation(atm.Id, user, now);
        var differences = atm.Reconcile(reconciliation.Id, counts);

        foreach (var line in differences)
        {
            var rate = ratesToTry.TryGetValue(line.Currency, out var r)
                ? r
                : throw new InvalidOperationException($"No exchange rate for {line.Currency}.");
            reconciliation._lines.Add(new ReconciliationLine(
                line.Slot, line.Currency, line.Denomination, line.Expected, line.Counted,
                (line.Counted - line.Expected) * line.Denomination * rate));
        }

        reconciliation.TotalDifferenceTry = reconciliation._lines.Sum(l => Math.Abs(l.DifferenceTry));
        if (reconciliation.TotalDifferenceTry > ApprovalThresholdTry)
        {
            reconciliation.Status = ReconciliationStatus.AwaitingApproval;
            atm.RequireReconciliationApproval(reconciliation.Id);
        }
        else
        {
            reconciliation.Status = ReconciliationStatus.Completed;
        }

        return reconciliation;
    }

    /// <summary>Four-eyes approval: the approver must not be the user who performed the count.</summary>
    public void Approve(Atm atm, string approvedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(atm);
        var user = Users.Require(approvedBy);

        if (Status != ReconciliationStatus.AwaitingApproval)
        {
            throw DomainException.Conflict(
                ErrorCodes.ReconciliationNotAwaitingApproval,
                $"Reconciliation {Id} is {Status} and does not need approval.");
        }

        if (Users.Same(user, PerformedBy))
        {
            throw DomainException.Forbidden(
                ErrorCodes.SameUserApproval,
                $"Reconciliation {Id} was performed by {PerformedBy}; a different user must approve it.");
        }

        atm.ClearReconciliationApproval(Id);
        Status = ReconciliationStatus.Approved;
        ApprovedBy = user;
        ApprovedAt = now;
    }
}
