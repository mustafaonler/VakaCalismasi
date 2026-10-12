using AtmCash.Domain.Atms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AtmCash.Infrastructure.Persistence.Configurations;

internal sealed class AtmConfiguration : IEntityTypeConfiguration<Atm>
{
    public void Configure(EntityTypeBuilder<Atm> builder)
    {
        builder.ToTable("atms");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Code).HasMaxLength(64);
        builder.HasIndex(a => a.Code).IsUnique();
        builder.Ignore(a => a.NewMovements);

        builder.HasMany(a => a.Cassettes)
            .WithOne()
            .HasForeignKey(c => c.AtmId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(a => a.Cassettes).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CassetteConfiguration : IEntityTypeConfiguration<Cassette>
{
    public void Configure(EntityTypeBuilder<Cassette> builder)
    {
        builder.ToTable("cassettes", t =>
            t.HasCheckConstraint("ck_cassettes_slot", "slot BETWEEN 1 AND 5"));
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasIndex(c => new { c.AtmId, c.Slot }).IsUnique();

        builder.OwnsMany(c => c.Holdings, holding =>
        {
            holding.ToTable("cassette_holdings", t =>
            {
                t.HasCheckConstraint("ck_cassette_holdings_count", $"count BETWEEN 0 AND {Cassette.Capacity}");
                t.HasCheckConstraint("ck_cassette_holdings_reserved", "reserved BETWEEN 0 AND count");
            });
            holding.WithOwner().HasForeignKey("CassetteId");
            holding.HasKey("CassetteId", nameof(CassetteHolding.Currency), nameof(CassetteHolding.Denomination));
            holding.Ignore(h => h.Available);
        });
        builder.Navigation(c => c.Holdings).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CassetteMovementConfiguration : IEntityTypeConfiguration<CassetteMovement>
{
    public void Configure(EntityTypeBuilder<CassetteMovement> builder)
    {
        builder.ToTable("cassette_movements");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.HasIndex(m => m.CassetteId);
        builder.HasIndex(m => m.CorrelationId);

        builder.HasOne<Atm>().WithMany().HasForeignKey(m => m.AtmId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Cassette>().WithMany().HasForeignKey(m => m.CassetteId).OnDelete(DeleteBehavior.Restrict);
    }
}
