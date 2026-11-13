using Dhole.Operations.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Operations.Persistence.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOperationsPersistence(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:Postgres must be configured.");
        services.AddDbContext<OperationsDbContext>(o => o.UseNpgsql(connection));
        services.AddScoped<IOutboxWriter, OperationsOutboxWriter>();
        return services;
    }
}

internal sealed class OperationsOutboxWriter(OperationsDbContext db) : IOutboxWriter
{
    public async Task<Guid> EnqueueAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default)
    {
        var entry = Dhole.Operations.Domain.Models.OutboxMessage.Create(eventName, payloadJson);
        db.OutboxMessages.Add(entry);
        // Business handlers must pass the same DbContext transaction to keep state and Outbox atomic.
        await db.SaveChangesAsync(cancellationToken);
        return entry.Id;
    }
}
