using Dhole.Operations.Infrastructure.DependencyInjection;
using Dhole.Operations.Persistence.DependencyInjection;
using Dhole.Operations.Workers.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOperationsPersistence(builder.Configuration);
builder.Services.AddOperationsInfrastructure(builder.Configuration);
builder.Services.AddHostedService<DatabaseMigrationService>();
builder.Services.AddHostedService<OperationsOutboxPublisher>();
builder.Services.AddHostedService<OperationsInboxConsumer>();
await builder.Build().RunAsync();
