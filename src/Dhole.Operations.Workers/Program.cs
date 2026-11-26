using Dhole.Operations.Infrastructure.DependencyInjection;
using Dhole.Operations.Application.DependencyInjection;
using Dhole.Operations.Persistence.DependencyInjection;
using Dhole.Operations.Workers.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOperationsApplication();
builder.Services.AddHttpClient<Dhole.Operations.Application.Cases.IOperationsAssignmentEligibility, Dhole.Operations.Infrastructure.OperationsAssignmentEligibility>(client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddOperationsPersistence(builder.Configuration);
builder.Services.AddHttpClient("crm-pricing-acceptance", client => client.Timeout = TimeSpan.FromSeconds(12));
builder.Services.AddOperationsInfrastructure(builder.Configuration);
builder.Services.AddHostedService<DatabaseMigrationService>();
builder.Services.AddHostedService<OperationsOutboxPublisher>();
builder.Services.AddHttpClient("auth-operations-team-permissions", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<OperationsTeamPermissionSyncWorker>();
builder.Services.AddHostedService<OperationsInboxConsumer>();
builder.Services.AddHttpClient("auth-operations-pool", client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHostedService<OperationsAutomaticAperturePoolWorker>();
builder.Services.AddHostedService<OperationsOriginAssignmentWorker>();
builder.Services.AddHostedService<OperationsPricingAcceptanceInboxWorker>();
builder.Services.AddHostedService<OperationsPricingReviewResolutionInboxWorker>();
await builder.Build().RunAsync();
