using Dhole.Operations.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Persistence;

public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxReceipt> InboxReceipts => Set<InboxReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("operations_outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnType("text").IsRequired();
            e.Property(x => x.LastError).HasColumnType("text");
            e.HasIndex(x => new { x.PublishedAtUtc, x.OccurredAtUtc });
        });
        modelBuilder.Entity<InboxReceipt>(e =>
        {
            e.ToTable("operations_inbox");
            e.HasKey(x => new { x.ConsumerName, x.EventId });
            e.Property(x => x.ConsumerName).HasMaxLength(100);
            e.Property(x => x.EventName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnType("text").IsRequired();
            e.HasIndex(x => x.ProcessedAtUtc);
        });
    }
}
