namespace AtmCash.Domain.Atms;

public enum MovementType
{
    Load,
    Unload,
    Withdrawal,
    Deposit,
    Reservation,
    ReservationRelease,
    ReconciliationAdjustment,
}

public static class MovementTypeExtensions
{
    /// <summary>
    /// Reservation movements change the reserved count; every other movement changes the
    /// physical note count. Replaying both kinds reproduces a holding exactly.
    /// </summary>
    public static bool AffectsReserved(this MovementType type) =>
        type is MovementType.Reservation or MovementType.ReservationRelease;
}
