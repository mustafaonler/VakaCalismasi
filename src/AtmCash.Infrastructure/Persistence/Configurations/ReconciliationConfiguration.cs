using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Reconciliations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence.Configurations;

internal sealed class ReconciliationConfiguration : IEntityTypeConfiguration<Reconciliation>
{
    public void Configure(EntityTypeBuilder<Reconciliation> builder)
    {
        builder.ToTable("reconciliations");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.PerformedBy).HasMaxLength(Users.MaxLength);
        builder.Property(r => r.ApprovedBy).HasMaxLength(Users.MaxLength);
        builder.Property(r => r.TotalDifferenceTry).HasPrecision(18, 4);
        builder.HasIndex(r => r.AtmId);
        builder.HasOne<Atm>().WithMany().HasForeignKey(r => r.AtmId).OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(r => r.Lines, line =>
        {
            line.ToTable("reconciliation_lines");
            line.WithOwner().HasForeignKey("ReconciliationId");
            line.Property(l => l.DifferenceTry).HasPrecision(18, 4);
            line.Ignore(l => l.Difference);
        });
        builder.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> builder)
    {
        builder.ToTable("exchange_rates", t => t.HasCheckConstraint("ck_exchange_rates_positive", "rate_to_try > 0"));
        builder.HasKey(r => r.Currency);
        builder.Property(r => r.RateToTry).HasPrecision(18, 6);

        // Illustrative seed rates (B9 allows seed data); operations would keep them current.
        builder.HasData(
            new ExchangeRate(Currency.TRY, 1m),
            new ExchangeRate(Currency.USD, 41.50m),
            new ExchangeRate(Currency.EUR, 48.25m));
    }
}
