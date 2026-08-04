using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SeatReservation.Domain.Outbox;

namespace SeatReservation.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Type).HasMaxLength(200).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);

        // jsonb rather than text: the payload stays queryable, which matters when you are
        // trying to work out what a stuck message actually contains.
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();

        // The dispatcher's query is exactly this. A partial index rather than a full one:
        // processed rows are the overwhelming majority and are never selected again, so
        // there is no reason to carry them in the index.
        builder.HasIndex(m => new { m.NextAttemptAt, m.OccurredAt })
            .HasDatabaseName("ix_outbox_pending")
            .HasFilter("\"ProcessedAt\" IS NULL AND \"IsDead\" = false");
    }
}
