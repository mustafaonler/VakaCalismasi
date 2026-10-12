using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Atms;

/// <summary>
/// Raised when the available notes of a recycling cassette fall below
/// <see cref="Cassette.LowCashThreshold"/> (B10). Written to the outbox in the transaction of the
/// operation that caused it.
/// </summary>
/// <param name="Trigger">The movement type of the change that crossed the threshold.</param>
/// <param name="CorrelationId">The quote, reconciliation or other operation behind the change, if any.</param>
public sealed record LowCashAlert(
    Guid AtmId,
    string AtmCode,
    int Slot,
    Currency Currency,
    int Denomination,
    int AvailableCount,
    int Threshold,
    MovementType Trigger,
    Guid? CorrelationId);
