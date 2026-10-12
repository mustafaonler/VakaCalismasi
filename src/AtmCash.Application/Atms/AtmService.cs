using AtmCash.Application.Abstractions;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Common;
using AtmCash.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Application.Atms;

/// <summary>ATM setup and lifecycle use cases (Part A #7, #12; B1, B2).</summary>
public sealed class AtmService(IAtmDbContext db, TimeProvider timeProvider)
{
    public async Task<AtmDetailsDto> CreateAsync(string code, CancellationToken cancellationToken)
    {
        var atm = Atm.Create(code);
        if (await db.Atms.AnyAsync(a => a.Code == atm.Code, cancellationToken))
        {
            throw DomainException.Conflict(ErrorCodes.AtmCodeTaken, $"An ATM with code {atm.Code} already exists.");
        }

        db.Atms.Add(atm);
        await db.SaveChangesAsync(cancellationToken);
        return atm.ToDetails();
    }

    public async Task<IReadOnlyList<AtmSummaryDto>> ListAsync(CancellationToken cancellationToken) =>
        await db.Atms
            .AsNoTracking()
            .OrderBy(a => a.Code)
            .Select(a => new AtmSummaryDto(a.Id, a.Code, a.Status))
            .ToListAsync(cancellationToken);

    public async Task<AtmDetailsDto> GetAsync(Guid atmId, CancellationToken cancellationToken) =>
        (await db.LoadAtmAsync(atmId, tracking: false, cancellationToken)).ToDetails();

    public Task<AtmDetailsDto> ConfigureCassettesAsync(
        Guid atmId, IReadOnlyList<RecyclingCassetteSpec> specs, CancellationToken cancellationToken) =>
        MutateAsync(atmId, atm => atm.ConfigureCassettes(specs), cancellationToken);

    public Task<AtmDetailsDto> LoadCashAsync(Guid atmId, IReadOnlyList<CassetteLoad> loads, CancellationToken cancellationToken) =>
        MutateAsync(atmId, atm => atm.LoadCash(loads), cancellationToken);

    public Task<AtmDetailsDto> UnloadAsync(Guid atmId, IReadOnlyList<int> slots, CancellationToken cancellationToken) =>
        MutateAsync(atmId, atm => atm.UnloadCassettes(slots), cancellationToken);

    /// <summary>
    /// Moves the ATM to IN_SERVICE or MAINTENANCE. Entering maintenance cancels open quotes so that
    /// no reserved notes remain while cassettes are counted or reconfigured.
    /// </summary>
    public Task<AtmDetailsDto> ChangeStatusAsync(Guid atmId, AtmStatus target, CancellationToken cancellationToken) =>
        MutateAsync(
            atmId,
            async atm =>
            {
                atm.ChangeStatus(target);
                if (target == AtmStatus.Maintenance)
                {
                    var now = timeProvider.GetUtcNow();
                    var open = await db.Quotes
                        .Where(q => q.AtmId == atmId && q.Status == QuoteStatus.Active)
                        .ToListAsync(cancellationToken);
                    open.ForEach(q => q.Cancel(atm, now));
                }
            },
            cancellationToken);

    private Task<AtmDetailsDto> MutateAsync(Guid atmId, Action<Atm> change, CancellationToken cancellationToken) =>
        MutateAsync(
            atmId,
            atm =>
            {
                change(atm);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>Runs a change on one ATM inside a transaction that holds the ATM's row lock.</summary>
    private async Task<AtmDetailsDto> MutateAsync(Guid atmId, Func<Atm, Task> change, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockAtmAsync(atmId, cancellationToken);
        var atm = await db.LoadAtmAsync(atmId, tracking: true, cancellationToken);

        await change(atm);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return atm.ToDetails();
    }
}
