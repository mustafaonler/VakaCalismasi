namespace AtmCash.Domain.Atms;

public enum CassetteKind
{
    /// <summary>Accepts and dispenses a single currency and denomination.</summary>
    Recycling,

    /// <summary>Accepts any note; never dispenses.</summary>
    Deposit,
}
