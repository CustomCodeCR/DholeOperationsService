namespace Dhole.Operations.Contracts.Events;
public sealed record OperationsEvent(Guid Id, string EventName, string JsonPayload, DateTimeOffset OccurredAtUtc);
