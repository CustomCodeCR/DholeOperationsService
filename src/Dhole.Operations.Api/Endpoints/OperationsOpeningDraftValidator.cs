using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>Compatibility facade; the shared domain policy is also used by the pool worker.</summary>
public static class OperationsOpeningDraftValidator
{
    public sealed record FieldStatus(string FieldCode, string CompletionStatus, string? Reason = null);
    public static string[] MissingFields(string modality, string? json, bool manualOpening = false) =>
        OperationsOpeningDraftPolicy.MissingFields(modality, json, manualOpening);
    public static IReadOnlyList<FieldStatus> EvaluateFields(string modality, string? json, bool manualOpening = false) =>
        OperationsOpeningDraftPolicy.EvaluateFields(modality, json, manualOpening)
            .Select(x => new FieldStatus(x.FieldCode, x.CompletionStatus, x.Reason)).ToArray();

    public static IReadOnlyList<FieldStatus> EvaluateCase(string modality, string? json, Guid accountId, bool manualOpening)
    {
        var fields = EvaluateFields(modality, json, manualOpening).ToList();
        fields.Insert(0, new("accountId", accountId == Guid.Empty ? "PENDIENTE_BLOQUEANTE" : "CONFIRMADO"));
        fields.Insert(1, new("modality", string.IsNullOrWhiteSpace(modality) ? "PENDIENTE_BLOQUEANTE" : "CONFIRMADO"));
        return fields;
    }
}
