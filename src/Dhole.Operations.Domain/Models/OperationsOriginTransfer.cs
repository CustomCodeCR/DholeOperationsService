namespace Dhole.Operations.Domain.Models;

/// <summary>Immutable evidence of the single-stage Origin handoff. A later
/// Config edit never changes this receipt.</summary>
public sealed class OperationsOriginTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Guid CaseId { get; set; }
    public Guid OriginStageId { get; set; }
    public Guid TransitStageId { get; set; }
    public Guid ActorUserId { get; set; }
    public long ExpectedCaseVersion { get; set; }
    public long ExpectedStageVersion { get; set; }
    public long ResultCaseVersion { get; set; }
    public Guid RuleId { get; set; }
    public int RuleVersion { get; set; }
    public string RuleFingerprint { get; set; } = string.Empty;
    public string RuleSnapshotJson { get; set; } = "{}";
    public string ChecklistJson { get; set; } = "{}";
    public DateTimeOffset TransferredAtUtc { get; set; }

    public bool Matches(Guid caseId, Guid actor, long caseVersion, long stageVersion,
        Guid ruleId, int ruleVersion, string? fingerprint) =>
        CaseId == caseId && ActorUserId == actor && ExpectedCaseVersion == caseVersion &&
        ExpectedStageVersion == stageVersion && RuleId == ruleId && RuleVersion == ruleVersion &&
        string.Equals(RuleFingerprint, fingerprint, StringComparison.Ordinal);
}
