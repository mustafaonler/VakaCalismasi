using System.Text.Json;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Deposits;

namespace AtmCash.Domain.Atms;

public sealed record RecyclingCassetteSpec(Currency Currency, int Denomination);

public sealed record CassetteLoad(int Slot, int Quantity);

/// <summary>A number of notes of one denomination taken from (or reserved in) one cassette.</summary>
public sealed record NoteAllocation(int Slot, int Denomination, int Quantity);

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

    /// <summary>Withdrawals and deposits are only possible while the ATM is in service.</summary>
    public void EnsureInService(string operation) => EnsureStatus(operation, AtmStatus.InService);

    /// <summary>Unreserved notes per denomination across the recycling cassettes of a currency.</summary>
    public IReadOnlyDictionary<int, int> AvailableNotes(Currency currency) =>
        RecyclingCassettes(currency)
            .GroupBy(c => c.Denomination!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Available));

    /// <summary>
    /// Reserves notes for a quote. When two cassettes hold the same denomination, the one with the
    /// higher fill ratio is drawn from first (B2). Reserved notes are already spoken for, so the ratio
    /// uses available notes; all cassettes share one capacity, so the most available notes is the
    /// highest ratio.
    /// </summary>
    public IReadOnlyList<NoteAllocation> ReserveNotes(Currency currency, IReadOnlyDictionary<int, int> notes, Guid quoteId)
    {
        ArgumentNullException.ThrowIfNull(notes);
        EnsureInService("request a withdrawal");

        var allocations = new List<NoteAllocation>();
        foreach (var (denomination, count) in notes.Where(n => n.Value > 0))
        {
            var remaining = count;
            var cassettes = RecyclingCassettes(currency)
                .Where(c => c.Denomination == denomination)
                .OrderByDescending(c => c.Available)
                .ThenBy(c => c.Slot);
            foreach (var cassette in cassettes)
            {
                var take = Math.Min(remaining, cassette.Available);
                if (take == 0)
                {
                    continue;
                }

                _newMovements.Add(cassette.Reserve(take, quoteId));
                allocations.Add(new NoteAllocation(cassette.Slot, denomination, take));
                remaining -= take;
            }

            if (remaining > 0)
            {
                throw DomainException.Conflict(
                    ErrorCodes.InsufficientNotes, $"Not enough {currency} {denomination} notes to reserve {count}.");
            }
        }

        return allocations;
    }

    /// <summary>Gives reserved notes back to the available pool (cancel, expiry, unchosen alternative).</summary>
    public void ReleaseReservations(IEnumerable<NoteAllocation> allocations, Guid quoteId)
    {
        ArgumentNullException.ThrowIfNull(allocations);
        foreach (var allocation in allocations.Where(a => a.Quantity > 0))
        {
            _newMovements.Add(GetCassette(allocation.Slot).ReleaseReservation(allocation.Quantity, quoteId));
        }
    }

    /// <summary>Pays out previously reserved notes.</summary>
    public void Dispense(IEnumerable<NoteAllocation> allocations, Guid quoteId)
    {
        ArgumentNullException.ThrowIfNull(allocations);
        EnsureInService("dispense cash");
        foreach (var allocation in allocations.Where(a => a.Quantity > 0))
        {
            _newMovements.AddRange(GetCassette(allocation.Slot).Dispense(allocation.Quantity, quoteId));
        }
    }

    /// <summary>
    /// Accepts a customer deposit (Part A #11, B7). Each recognised note goes to the recycling
    /// cassette of its currency and denomination; if there is none, or it is full, the note goes to
    /// the deposit cassette; if that is full too, the note is returned. Unrecognised notes are
    /// returned and never touch a cassette.
    /// </summary>
    /// <remarks>
    /// When two recycling cassettes match, the emptier one is filled first (mirroring withdrawals,
    /// which drain the fuller one first). Lines are routed in request order, which only decides
    /// which notes are returned when the deposit cassette runs out of room, not how many.
    /// </remarks>
    public DepositReceipt Deposit(Guid depositId, IReadOnlyList<DepositNote> notes, int unrecognisedCount)
    {
        ArgumentNullException.ThrowIfNull(notes);
        EnsureInService("accept a deposit");
        ValidateDeposit(notes, unrecognisedCount);

        var placements = new List<DepositPlacement>();
        var returned = new List<ReturnedNotes>();
        var deposit = GetCassette(DepositSlot);

        // Several lines for the same note are treated as one.
        var lines = notes
            .GroupBy(n => (n.Currency, n.Denomination))
            .Select(g => new DepositNote(g.Key.Currency, g.Key.Denomination, g.Sum(n => n.Count)));

        foreach (var line in lines)
        {
            var remaining = line.Count;
            var targets = _cassettes
                .Where(c => c.IsConfiguredFor(line.Currency, line.Denomination))
                .OrderByDescending(c => c.FreeSpace)
                .ThenBy(c => c.Slot)
                .Append(deposit);

            foreach (var cassette in targets)
            {
                var fits = Math.Min(remaining, cassette.FreeSpace);
                if (fits == 0)
                {
                    continue;
                }

                _newMovements.Add(cassette.Add(MovementType.Deposit, line.Currency, line.Denomination, fits, depositId));
                placements.Add(new DepositPlacement(cassette.Slot, cassette.Kind, line.Currency, line.Denomination, fits));
                remaining -= fits;
            }

            if (remaining > 0)
            {
                returned.Add(new ReturnedNotes(ReturnReason.CassetteFull, line.Currency, line.Denomination, remaining));
            }
        }

        if (unrecognisedCount > 0)
        {
            returned.Add(new ReturnedNotes(ReturnReason.Unrecognised, null, null, unrecognisedCount));
        }

        return new DepositReceipt(depositId, placements, returned);
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

    public void PutInService() => TransitionTo(AtmStatus.InService, AtmStatus.Loaded, AtmStatus.Maintenance);

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

    private static void ValidateDeposit(IReadOnlyList<DepositNote> notes, int unrecognisedCount)
    {
        if (unrecognisedCount < 0)
        {
            throw DomainException.Validation(ErrorCodes.InvalidQuantity, "Unrecognised note count cannot be negative.");
        }

        if (notes.Any(n => n.Count <= 0))
        {
            throw DomainException.Validation(ErrorCodes.InvalidQuantity, "Every deposited note line must have a positive count.");
        }

        var invalid = notes.FirstOrDefault(n => !Denominations.IsValid(n.Denomination));
        if (invalid is not null)
        {
            throw DomainException.Validation(
                ErrorCodes.InvalidDenomination,
                $"Unsupported denomination {invalid.Denomination}; allowed: {string.Join(", ", Denominations.All)}.");
        }

        if (notes.Count == 0 && unrecognisedCount == 0)
        {
            throw DomainException.Validation(ErrorCodes.EmptyDeposit, "A deposit must contain at least one note.");
        }
    }

    private IEnumerable<Cassette> RecyclingCassettes(Currency currency) =>
        _cassettes.Where(c => c.Kind == CassetteKind.Recycling && c.Currency == currency);

    private static string StatusNames(AtmStatus[] statuses) => string.Join(", ", statuses.Select(StatusName));
}
