using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence.Configurations;

internal sealed class WithdrawalQuoteConfiguration : IEntityTypeConfiguration<WithdrawalQuote>
{
    public void Configure(EntityTypeBuilder<WithdrawalQuote> builder)
    {
        builder.ToTable("withdrawal_quotes");
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();
        builder.Property(q => q.CardHash).HasMaxLength(64);

        // Daily limit lookups and expiry sweeps.
        builder.HasIndex(q => new { q.CardHash, q.Currency, q.Status });
        builder.HasIndex(q => new { q.Status, q.ExpiresAt });
        builder.HasIndex(q => new { q.AtmId, q.Status });

        builder.HasOne<Atm>().WithMany().HasForeignKey(q => q.AtmId).OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(q => q.OfferLines, line =>
        {
            line.ToTable("withdrawal_quote_offers");
            line.WithOwner().HasForeignKey("QuoteId");
        });
        builder.Navigation(q => q.OfferLines).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(q => q.Reservations, reservation =>
        {
            reservation.ToTable("withdrawal_quote_reservations");
            reservation.WithOwner().HasForeignKey("QuoteId");
        });
        builder.Navigation(q => q.Reservations).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(r => r.Key);
        builder.Property(r => r.Key).HasMaxLength(128);
        builder.Property(r => r.Response).HasColumnType("jsonb");
        builder.HasOne<WithdrawalQuote>().WithMany().HasForeignKey(r => r.QuoteId).OnDelete(DeleteBehavior.Restrict);
    }
}
