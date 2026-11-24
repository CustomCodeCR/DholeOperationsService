using Dhole.Operations.Infrastructure.DependencyInjection;
using Dhole.Operations.Application.DependencyInjection;
using Dhole.Operations.Persistence.DependencyInjection;
using Dhole.Operations.Workers.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOperationsApplication();
builder.Services.AddOperationsPersistence(builder.Configuration);
builder.Services.AddHttpClient("crm-pricing-acceptance", client => client.Timeout = TimeSpan.FromSeconds(12));
builder.Services.AddOperationsInfrastructure(builder.Configuration);
builder.Services.AddHostedService<DatabaseMigrationService>();
builder.Services.AddHostedService<OperationsOutboxPublisher>();
builder.Services.AddHostedService<OperationsInboxConsumer>();
builder.Services.AddHostedService<OperationsOriginAssignmentWorker>();
builder.Services.AddHostedService<OperationsPricingAcceptanceInboxWorker>();
builder.Services.AddHostedService<OperationsPricingReviewResolutionInboxWorker>();
await builder.Build().RunAsync();
