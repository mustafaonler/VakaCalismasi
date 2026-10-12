using AtmCash.Application.Abstractions;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Application.Atms;

/// <summary>ATM setup and lifecycle use cases (Part A #7, #12; B1, B2).</summary>
public sealed class AtmService(IAtmDbContext db)
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
        (await LoadAsync(atmId, tracking: false, cancellationToken)).ToDetails();

    public Task<AtmDetailsDto> ConfigureCassettesAsync(
        Guid atmId, IReadOnlyList<RecyclingCassetteSpec> specs, CancellationToken cancellationToken) =>
        MutateAsync(atmId, atm => atm.ConfigureCassettes(specs), cancellationToken);

    public Task<AtmDetailsDto> LoadCashAsync(Guid atmId, IReadOnlyList<CassetteLoad> loads, CancellationToken cancellationToken) =>
        MutateAsync(atmId, atm => atm.LoadCash(loads), cancellationToken);

    public Task<AtmDetailsDto> UnloadAsync(Guid atmId, IReadOnlyList<int> slots, CancellationToken cancellationToken) =>
        MutateAsync(atmId, atm => atm.UnloadCassettes(slots), cancellationToken);

    public Task<AtmDetailsDto> ChangeStatusAsync(Guid atmId, AtmStatus target, CancellationToken cancellationToken) =>
        MutateAsync(atmId, atm => atm.ChangeStatus(target), cancellationToken);

    /// <summary>Runs a change on one ATM inside a transaction that holds the ATM's row lock.</summary>
    private async Task<AtmDetailsDto> MutateAsync(Guid atmId, Action<Atm> change, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockAtmAsync(atmId, cancellationToken);
        var atm = await LoadAsync(atmId, tracking: true, cancellationToken);

        change(atm);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return atm.ToDetails();
    }

    private async Task<Atm> LoadAsync(Guid atmId, bool tracking, CancellationToken cancellationToken)
    {
        var query = db.Atms.Include(a => a.Cassettes).AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(a => a.Id == atmId, cancellationToken)
            ?? throw DomainException.NotFound(ErrorCodes.AtmNotFound, $"ATM {atmId} was not found.");
    }
}
