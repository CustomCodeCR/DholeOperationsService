using Dhole.Operations.Domain.Models;

namespace Dhole.Operations.Application.Cases;

public sealed record CreateManualCaseCommand(
    Guid CompanyId, string SeriesCode, Guid AccountId, Guid ExecutiveUserId,
    string ModalityCode, short Priority, decimal ComplexityPoints, Guid ActorUserId,
    string? OriginCountryCode = null, string? DestinationCountryCode = null);

public sealed record AssignStageCommand(
    Guid CaseId, Guid StageId, Guid ActorUserId);

public sealed record RegisterDocumentCommand(
    Guid CaseId, string DocumentTypeCode, Guid StorageFileId,
    string FileName, string MimeType, string? Sha256, Guid ActorUserId);

public sealed record QueueDocumentExtractionCommand(Guid CaseId, Guid DocumentVersionId, Guid ActorUserId);

public sealed record CaseCreatedResult(Guid Id, string Idtra, string Status, Guid InitialStageId);
public sealed record StageAssignedResult(Guid AssignmentId, Guid TeamId, Guid UserId, decimal RelativeLoad);
public sealed record DocumentRegisteredResult(Guid DocumentId, Guid VersionId);
public sealed record DocumentExtractionQueuedResult(Guid JobId, string Status);

public interface IOperationsCaseStore
{
    Task<CaseCreatedResult> CreateManualAsync(CreateManualCaseCommand command, CancellationToken ct);
    Task<StageAssignedResult?> AssignStageAsync(AssignStageCommand command, CancellationToken ct);
    Task<DocumentRegisteredResult> RegisterDocumentAsync(RegisterDocumentCommand command, CancellationToken ct);
    Task<DocumentExtractionQueuedResult> QueueDocumentExtractionAsync(QueueDocumentExtractionCommand command, CancellationToken ct);
}

public sealed class CreateManualCaseHandler(IOperationsCaseStore store)
{
    public Task<CaseCreatedResult> HandleAsync(CreateManualCaseCommand command, CancellationToken ct)
    {
        if (command.CompanyId == Guid.Empty || command.AccountId == Guid.Empty ||
            command.ExecutiveUserId == Guid.Empty || command.ActorUserId == Guid.Empty)
            throw new ArgumentException("Company, CRM account, executive and actor must be provided.");
        if (command.SeriesCode.Length != 5 || !command.SeriesCode.All(char.IsAsciiDigit))
            throw new ArgumentException("The explicit IDTRA series must contain exactly five digits.");
        if (string.IsNullOrWhiteSpace(command.ModalityCode) || command.ModalityCode.Length > 30)
            throw new ArgumentException("A recognized modality code is required.");
        if (command.Priority is < 1 or > 5 || command.ComplexityPoints <= 0 || command.ComplexityPoints > 1000)
            throw new ArgumentException("Priority or complexity points out of range.");
        if (!IsCountryCode(command.OriginCountryCode) || !IsCountryCode(command.DestinationCountryCode))
            throw new ArgumentException("Country codes must be two ISO alphabetic characters when provided.");
        return store.CreateManualAsync(command, ct);
    }

    private static bool IsCountryCode(string? value) => value is null ||
        (value.Length == 2 && value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'));
}

public sealed class AssignStageHandler(IOperationsCaseStore store)
{
    public Task<StageAssignedResult?> HandleAsync(AssignStageCommand command, CancellationToken ct) =>
        store.AssignStageAsync(command, ct);
}

public sealed class RegisterDocumentHandler(IOperationsCaseStore store)
{
    public Task<DocumentRegisteredResult> HandleAsync(RegisterDocumentCommand command, CancellationToken ct)
    {
        if (command.StorageFileId == Guid.Empty || command.CaseId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.DocumentTypeCode) || command.DocumentTypeCode.Length > 60 ||
            string.IsNullOrWhiteSpace(command.FileName) || command.FileName.Length > 255 ||
            string.IsNullOrWhiteSpace(command.MimeType) || command.MimeType.Length > 150)
            throw new ArgumentException("Valid Storage file reference, case, document type and metadata required.");
        return store.RegisterDocumentAsync(command, ct);
    }
}

public sealed class QueueDocumentExtractionHandler(IOperationsCaseStore store)
{
    public Task<DocumentExtractionQueuedResult> HandleAsync(QueueDocumentExtractionCommand command, CancellationToken ct) =>
        store.QueueDocumentExtractionAsync(command, ct);
}
