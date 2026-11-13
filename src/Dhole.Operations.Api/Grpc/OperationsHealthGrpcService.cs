using Dhole.Operations.Contracts.Grpc;
using Grpc.Core;

namespace Dhole.Operations.Api.Grpc;

public sealed class OperationsHealthGrpcService : OperationsHealth.OperationsHealthBase
{
    public override Task<HealthReply> Check(HealthRequest request, ServerCallContext context) =>
        Task.FromResult(new HealthReply
        {
            Service = "DholeOperationsService",
            Status = "Healthy",
            Timestamp = DateTimeOffset.UtcNow.ToString("O")
        });
}
