using Dhole.Operations.Application.Cases;
using Microsoft.Extensions.DependencyInjection;

namespace Dhole.Operations.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOperationsApplication(this IServiceCollection services)
    {
        services.AddScoped<CreateManualCaseHandler>();
        services.AddScoped<AssignStageHandler>();
        services.AddScoped<RegisterDocumentHandler>();
        services.AddScoped<QueueDocumentExtractionHandler>();
        return services;
    }
}
