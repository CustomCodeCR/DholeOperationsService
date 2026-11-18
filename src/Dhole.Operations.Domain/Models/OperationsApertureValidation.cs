namespace Dhole.Operations.Domain.Models;

public sealed class OperationsApertureValidation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseStageId { get; set; }
    public OperationsCaseStage CaseStage { get; set; } = null!;
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public short Slot { get; set; }
    public string Status { get; set; } = "PENDING";
    public Guid AssignedBy { get; set; }
    public DateTimeOffset AssignedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? Comment { get; set; }
}
public sealed class OperationsTeamAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public OperationsTeam Team { get; set; } = null!;
    public Guid EventId { get; set; }
    public Guid ActorUserId { get; set; }
    public string ActionCode { get; set; } = string.Empty;
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
