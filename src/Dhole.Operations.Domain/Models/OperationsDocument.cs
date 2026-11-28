namespace Dhole.Operations.Domain.Models;

/// <summary>Only Storage identifiers and metadata are retained; no binary data is stored here.</summary>
public sealed class OperationsDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public OperationsCase Case { get; set; } = null!;
    public string DocumentTypeCode { get; set; } = string.Empty;
    // Historical references have no proven owner or classification; remain null.
    public Guid? OwnerStageId { get; set; }
    public OperationsCaseStage? OwnerStage { get; set; }
    public string? AccessClass { get; set; }
    public long Version { get; set; } = 1;
    public string Status { get; set; } = "PENDING_REVIEW";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<OperationsDocumentVersion> Versions { get; set; } = [];
}

public sealed class OperationsDocumentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public OperationsDocument Document { get; set; } = null!;
    public int VersionNumber { get; set; }
    public Guid StorageFileId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string ReviewStatus { get; set; } = "PENDING";
}

public sealed class OperationsDocumentExtractionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentVersionId { get; set; }
    public OperationsDocumentVersion DocumentVersion { get; set; } = null!;
    public Guid? ExternalExtractionJobId { get; set; }
    public string Status { get; set; } = "QUEUED";
    public string? ExtractedJson { get; set; }
    public string? ValidationErrorsJson { get; set; }
    public Guid RequestedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAtUtc { get; set; }
}
