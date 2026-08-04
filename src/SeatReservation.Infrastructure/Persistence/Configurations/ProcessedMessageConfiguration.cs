using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SeatReservation.Domain.Outbox;

namespace SeatReservation.Infrastructure.Persistence.Configurations;

public sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        // Composite key on (message, consumer): the same message handled by two different
        // consumers is two legitimate rows, while the same pair twice is the duplicate the
        // key is here to reject.
        builder.HasKey(m => new { m.Id, m.Consumer });

        builder.Property(m => m.Consumer).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Type).HasMaxLength(200).IsRequired();

        // Retention: this table only grows. An operator prunes it by age, which is safe
        // once a row is older than the broker could possibly redeliver.
        builder.HasIndex(m => m.ProcessedAt);
    }
}
