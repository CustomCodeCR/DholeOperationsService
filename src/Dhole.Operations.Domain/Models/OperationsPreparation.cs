namespace Dhole.Operations.Domain.Models;

/// <summary>Operational state is separate from case, customer, and commercial state.</summary>
public sealed class OperationsOriginProgress
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseStageId { get; set; }
    public OperationsCaseStage CaseStage { get; set; } = null!;
    public string StatusCode { get; set; } = "ORIGIN_COORDINATION";
    public DateTimeOffset? ActualDepartureAtUtc { get; set; }
    public Guid? DepartureEventId { get; set; }
    public string? DepartureSource { get; set; }
    public Guid? EvidenceStorageFileId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; } = 1;
}

/// <summary>Private operational notes: not chat messages, notifications or public audit payloads.</summary>
public sealed class OperationsInternalNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public Guid? CaseStageId { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; } = 1;
}

/// <summary>Connection registration is not approval or proof that an external API works.</summary>
public sealed class OperationsCarrierConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string CarrierCode { get; set; } = string.Empty;
    public Guid? SrmProviderId { get; set; }
    public string Environment { get; set; } = "UAT";
    public string Status { get; set; } = "REQUESTED";
    public DateTimeOffset? LastValidatedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<OperationsCarrierCapability> Capabilities { get; set; } = [];
}

public sealed class OperationsCarrierCapability
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public OperationsCarrierConnection Connection { get; set; } = null!;
    public string ProductCode { get; set; } = string.Empty;
    public string CapabilityCode { get; set; } = string.Empty;
    public string Status { get; set; } = "UNVERIFIED";
    public string? ApiVersion { get; set; }
    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public string? EvidenceRef { get; set; }
}

public static class OperationsCarrierCodes
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "MAERSK", "YANG_MING", "OOCL", "MSC", "ONE", "COSCO",
        "EVERGREEN", "HAPAG_LLOYD", "PIL", "ZIM", "CMA_CGM", "HMM"
    };

    public static IReadOnlyList<string> Candidates => Known.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // Validation of an integration adapter code, NOT an SRM provider directory.
    public static bool IsKnown(string? code) => code is not null && Known.Contains(code);
}
