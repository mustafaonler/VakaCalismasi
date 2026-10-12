using System.Text.Json;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;

namespace AtmCash.Domain.Atms;

public sealed record RecyclingCassetteSpec(Currency Currency, int Denomination);

public sealed record CassetteLoad(int Slot, int Quantity);

/// <summary>
/// Aggregate root: an ATM, its five cassettes and its lifecycle (B2). All cash changes go
/// through this class; the movements they produce are collected in <see cref="NewMovements"/>
/// and persisted in the same transaction.
/// </summary>
public sealed class Atm
{
    public const int RecyclingCassetteCount = 4;
    public const int DepositSlot = 5;
    public const int MinForeignCassettes = 1;
    public const int MaxForeignCassettes = 2;

    private readonly List<Cassette> _cassettes = [];
    private readonly List<CassetteMovement> _newMovements = [];

    private Atm()
    {
        Code = null!;
    }

    private Atm(string code)
    {
        Id = Guid.CreateVersion7();
        Code = code;
        Status = AtmStatus.Unconfigured;
    }

    public Guid Id { get; private set; }

    /// <summary>Human-readable unique identifier, e.g. "IST-0001".</summary>
    public string Code { get; private set; }

    public AtmStatus Status { get; private set; }

    public IReadOnlyList<Cassette> Cassettes => _cassettes;

    /// <summary>Movements produced since the aggregate was loaded; not mapped.</summary>
    public IReadOnlyList<CassetteMovement> NewMovements => _newMovements;

    public static Atm Create(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw DomainException.Validation(ErrorCodes.InvalidAtmCode, "ATM code is required.");
        }

        return new Atm(code.Trim());
    }

    /// <summary>Hands the pending movements to the persistence layer and clears them.</summary>
    public IReadOnlyList<CassetteMovement> DequeueNewMovements()
    {
        var movements = _newMovements.ToList();
        _newMovements.Clear();
        return movements;
    }

    public Cassette GetCassette(int slot) =>
        _cassettes.SingleOrDefault(c => c.Slot == slot)
        ?? throw DomainException.NotFound(ErrorCodes.CassetteNotFound, $"ATM {Code} has no cassette in slot {slot}.");

    /// <summary>
    /// Sets the currency and denomination of the four recycling cassettes (slots 1-4, in order).
    /// Slot 5 is always the deposit cassette. A cassette whose configuration changes must be empty.
    /// </summary>
    public void ConfigureCassettes(IReadOnlyList<RecyclingCassetteSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        EnsureStatus("configure cassettes", AtmStatus.Unconfigured, AtmStatus.Configured, AtmStatus.Maintenance);

        if (specs.Count != RecyclingCassetteCount)
        {
            throw DomainException.Validation(
                ErrorCodes.InvalidCassetteConfiguration,
                $"Exactly {RecyclingCassetteCount} recycling cassettes must be configured, got {specs.Count}.");
        }

        var invalid = specs.FirstOrDefault(s => !Denominations.IsValid(s.Denomination));
        if (invalid is not null)
        {
            throw DomainException.Validation(
                ErrorCodes.InvalidDenomination,
                $"Unsupported denomination {invalid.Denomination}; allowed: {string.Join(", ", Denominations.All)}.");
        }

        var foreign = specs.Count(s => s.Currency != Currency.TRY);
        if (foreign is < MinForeignCassettes or > MaxForeignCassettes)
        {
            throw DomainException.Validation(
                ErrorCodes.ForeignCassetteCount,
                $"Between {MinForeignCassettes} and {MaxForeignCassettes} recycling cassettes must hold a foreign currency, got {foreign}.");
        }

        if (_cassettes.Count == 0)
        {
            for (var i = 0; i < specs.Count; i++)
            {
                _cassettes.Add(new Cassette(Id, i + 1, CassetteKind.Recycling, specs[i].Currency, specs[i].Denomination));
            }

            _cassettes.Add(new Cassette(Id, DepositSlot, CassetteKind.Deposit, null, null));
        }
        else
        {
            for (var i = 0; i < specs.Count; i++)
            {
                var cassette = GetCassette(i + 1);
                if (!cassette.IsConfiguredFor(specs[i].Currency, specs[i].Denomination))
                {
                    cassette.Reconfigure(specs[i].Currency, specs[i].Denomination);
                }
            }
        }

        if (Status == AtmStatus.Unconfigured)
        {
            Status = AtmStatus.Configured;
        }
    }

    /// <summary>Records cash loaded by hand into recycling cassettes.</summary>
    public void LoadCash(IReadOnlyList<CassetteLoad> loads)
    {
        ArgumentNullException.ThrowIfNull(loads);
        EnsureStatus("load cash", AtmStatus.Configured, AtmStatus.Loaded, AtmStatus.Maintenance);
        if (loads.Count == 0)
        {
            throw DomainException.Validation(ErrorCodes.InvalidQuantity, "At least one cassette load is required.");
        }

        foreach (var load in loads)
        {
            var cassette = GetCassette(load.Slot);
            if (cassette.Kind == CassetteKind.Deposit)
            {
                throw DomainException.Validation(
                    ErrorCodes.DepositCassetteNotLoadable, "The deposit cassette cannot be loaded with cash.");
            }

            _newMovements.Add(cassette.Add(
                MovementType.Load, cassette.Currency!.Value, cassette.Denomination!.Value, load.Quantity));
        }

        if (Status == AtmStatus.Configured)
        {
            Status = AtmStatus.Loaded;
        }
    }

    /// <summary>Empties the given cassettes completely (e.g. collecting the deposit cassette).</summary>
    public void UnloadCassettes(IReadOnlyList<int> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        EnsureStatus("unload cash", AtmStatus.Configured, AtmStatus.Loaded, AtmStatus.Maintenance);
        foreach (var slot in slots.Distinct())
        {
            _newMovements.AddRange(GetCassette(slot).RemoveAll(MovementType.Unload));
        }
    }

    /// <summary>
    /// Explicit lifecycle transition requested by an operator. Only IN_SERVICE and MAINTENANCE can be
    /// requested directly; CONFIGURED and LOADED are reached by configuring and loading.
    /// </summary>
    public void ChangeStatus(AtmStatus target)
    {
        switch (target)
        {
            case AtmStatus.InService:
                PutInService();
                break;
            case AtmStatus.Maintenance:
                EnterMaintenance();
                break;
            default:
                throw DomainException.Conflict(
                    ErrorCodes.InvalidStateTransition,
                    $"{StatusName(target)} cannot be requested directly; it is reached by configuring or loading the ATM.");
        }
    }

    public void PutInService() =>TransitionTo(AtmStatus.InService, AtmStatus.Loaded, AtmStatus.Maintenance);

    public void EnterMaintenance() => TransitionTo(AtmStatus.Maintenance, AtmStatus.InService);

    public static string StatusName(AtmStatus status) => JsonNamingPolicy.SnakeCaseUpper.ConvertName(status.ToString());

    private void TransitionTo(AtmStatus target, params AtmStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw DomainException.Conflict(
                ErrorCodes.InvalidStateTransition,
                $"ATM {Code} cannot move from {StatusName(Status)} to {StatusName(target)}; allowed from: {StatusNames(allowedFrom)}.");
        }

        Status = target;
    }

    private void EnsureStatus(string operation, params AtmStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw DomainException.Conflict(
                ErrorCodes.OperationNotAllowedInState,
                $"Cannot {operation} while ATM {Code} is {StatusName(Status)}; allowed in: {StatusNames(allowed)}.");
        }
    }

    private static string StatusNames(AtmStatus[] statuses) => string.Join(", ", statuses.Select(StatusName));
}
