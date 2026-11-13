namespace Dhole.Operations.Application.Abstractions;

/// <summary>Cross-layer CQRS markers used by the Operations application layer.</summary>
public interface IOperationsCommand<TResponse> {}
public interface IOperationsQuery<TResponse> {}
public interface IOutboxWriter
{
    Task<Guid> EnqueueAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default);
}
