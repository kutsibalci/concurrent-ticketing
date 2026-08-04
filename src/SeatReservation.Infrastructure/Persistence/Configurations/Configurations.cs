using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SeatReservation.Domain.Entities;

namespace SeatReservation.Infrastructure.Persistence.Configurations;

public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Venue).HasMaxLength(200).IsRequired();
        builder.Property(e => e.StartsAt).IsRequired();

        builder.HasIndex(e => e.StartsAt);

        builder.HasMany(e => e.Seats)
            .WithOne()
            .HasForeignKey(s => s.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(e => e.Seats).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class SeatConfiguration : IEntityTypeConfiguration<Seat>
{
    public void Configure(EntityTypeBuilder<Seat> builder)
    {
        builder.ToTable("seats");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Row).HasMaxLength(4).IsRequired();
        builder.Property(s => s.Price).HasPrecision(10, 2);
        builder.Property(s => s.Status).HasConversion<int>();

        // The same seat cannot exist twice in one event, whatever the application layer does.
        builder.HasIndex(s => new { s.EventId, s.Row, s.Number }).IsUnique();

        // Serving the seat map filters on this pair.
        builder.HasIndex(s => new { s.EventId, s.Status });

        // xmin is a PostgreSQL system column that changes on every update of the row.
        // Mapping it as a concurrency token turns "UPDATE seats SET status = ..." into
        // "UPDATE seats SET status = ... WHERE id = @id AND xmin = @version": if another
        // transaction touched the row first, zero rows match, EF raises
        // DbUpdateConcurrencyException, and the second writer loses instead of
        // overwriting. No table locks, no SELECT FOR UPDATE.
        builder.Property(s => s.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

public sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("reservations");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.Status).HasConversion<int>();
        builder.Property(r => r.TotalPrice).HasPrecision(10, 2);

        builder.HasMany(r => r.Seats)
            .WithOne()
            .HasForeignKey(s => s.ReservationId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Navigation(r => r.Seats).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(r => r.UserId);
        builder.HasIndex(r => r.EventId);

        // The sweeper's query is exactly this pair, and it runs every minute.
        builder.HasIndex(r => new { r.Status, r.HoldExpiresAt });
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.Email).HasMaxLength(256).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(120).IsRequired();
        builder.Property(u => u.Role).HasMaxLength(32).IsRequired();

        // Email is stored lower-cased, so this index also stops two accounts differing
        // only in capitalisation.
        builder.HasIndex(u => u.Email).IsUnique();

        builder.HasMany(u => u.RefreshTokens)
            .WithOne(t => t.User!)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(u => u.RefreshTokens).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);

        // Every key in this model is assigned by the domain, never by the database.
        // Leaving it as ValueGeneratedOnAdd makes EF treat an entity that already has an
        // id as one that already exists: a token added to a tracked user was marked
        // Modified instead of Added, producing an UPDATE against a row that was never
        // inserted, which surfaced as a concurrency exception on registration.
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();

        // Refresh is a lookup by hash on every token exchange.
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.UserId);
    }
}
