namespace Dhole.Operations.Domain.Models;

/// <summary>Operations owns the execution record, never the source CRM/Pricing entities.</summary>
public sealed class OperationsCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string Idtra { get; set; } = string.Empty;
    public string SeriesCode { get; set; } = string.Empty;
    public long SequenceNumber { get; set; }
    public Guid AccountId { get; set; }
    public Guid ExecutiveUserId { get; set; }
    public Guid? SourceQuoteId { get; set; }
    public Guid? SourceQuoteOptionId { get; set; }
    public string ApertureType { get; set; } = "MANUAL";
    public string ModalityCode { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING_VALIDATION";
    public short Priority { get; set; } = 1;
    public decimal ComplexityPoints { get; set; } = 1m;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long Version { get; set; } = 1;
    public List<OperationsCaseStage> Stages { get; set; } = [];
}

public sealed class OperationsCaseSequence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string SeriesCode { get; set; } = string.Empty;
    public long LastNumber { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OperationsCaseStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public string DepartmentCode { get; set; } = "APERTURAS";
    public string Status { get; set; } = "PENDING";
    public Guid? TeamId { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class OperationsTeam
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string DepartmentCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public List<OperationsTeamMember> Members { get; set; } = [];
}

public sealed class OperationsTeamMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public OperationsTeam Team { get; set; } = null!;
    public Guid UserId { get; set; }
    public bool IsSupervisor { get; set; }
    public bool IsAvailable { get; set; } = true;
    public decimal CapacityPoints { get; set; } = 10m;
}

public sealed class OperationsAssignmentRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public OperationsTeam Team { get; set; } = null!;
    public string DepartmentCode { get; set; } = string.Empty;
    public string? ModalityCode { get; set; }
    public string? OriginCountryCode { get; set; }
    public string? DestinationCountryCode { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class OperationsStageAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseStageId { get; set; }
    public OperationsCaseStage CaseStage { get; set; } = null!;
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public decimal AssignedPoints { get; set; }
    public Guid AssignedBy { get; set; }
    public DateTimeOffset AssignedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReleasedAtUtc { get; set; }
}
