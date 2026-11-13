using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Dhole.Operations.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOperationsInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connection = config["Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Redis:ConnectionString must be configured.");
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(connection);
            options.AbortOnConnectFail = false;
            options.ConnectRetry = 3;
            return ConnectionMultiplexer.Connect(options);
        });
        return services;
    }
}
