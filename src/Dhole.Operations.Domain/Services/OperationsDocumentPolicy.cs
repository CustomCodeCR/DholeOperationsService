namespace Dhole.Operations.Domain.Services;

/// <summary>Conservative initial allowlist. Historical and commercial documents
/// are never made transversal by guessing from a file name or MIME type.</summary>
public static class OperationsDocumentPolicy
{
    public const string Operational = "OPERATIONAL";
    public const string StorageSource = "DholeOperationsService";
    public const string StorageEntityType = "OperationsCaseStageDocument";

    public static string? NormalizeType(string? value)
    {
        var code = value?.Trim().ToUpperInvariant();
        return code is "HBL" or "MBL" or "BOOKING" or "SHIPPING_ORDER" ? code : null;
    }

    public static bool SupportsOwner(string? department) =>
        department is "APERTURAS" or "COORDINACION_ORIGEN" or "TRANSITO" or "PANAMA" or "COSTA_RICA";

    public static bool CanReadTransversally(Guid caseId, Guid documentCaseId, Guid? ownerStageId,
        Guid? ownerCaseId, string? accessClass, string? type, string? department) =>
        caseId != Guid.Empty && caseId == documentCaseId && ownerStageId.HasValue &&
        ownerStageId.Value != Guid.Empty && ownerCaseId == caseId && accessClass == Operational &&
        NormalizeType(type) == type && type is not null && SupportsOwner(department);

    public static bool CanRegister(Guid caseId, Guid ownerCaseId, string? department, string? stageStatus,
        string? caseStatus, string? type) =>
        caseId != Guid.Empty && caseId == ownerCaseId && SupportsOwner(department) &&
        stageStatus is "READY" or "IN_PROGRESS" &&
        caseStatus is not ("CANCELLED" or "COMPLETED") && NormalizeType(type) is not null;
}
