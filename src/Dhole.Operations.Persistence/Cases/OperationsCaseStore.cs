using Dhole.Operations.Persistence.Audit;
using System.Data;
using System.Text.Json;
using Dhole.Operations.Application.Cases;
using Dhole.Operations.Persistence.Messaging;
using Dhole.Operations.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Dhole.Operations.Persistence.Cases;

/// <summary>Transactional Operations writes. External CRM/Pricing/Storage identifiers are references only.</summary>
public sealed class OperationsCaseStore(OperationsDbContext db) : IOperationsCaseStore
{
    public async Task<CaseCreatedResult> CreateManualAsync(CreateManualCaseCommand input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var next = await NextSequenceAsync("00000", ct);
        // One global, server-owned counter. Minimum five digits, never a series cap.
        var idtra = $"IDTRA{next:D5}";
        var operation = new OperationsCase
        {
            CompanyId = input.CompanyId,
            SeriesCode = "00000",
            OpeningDataJson = input.OpeningDataJson ?? "{}",
            SequenceNumber = next,
            Idtra = idtra,
            AccountId = input.AccountId,
            ExecutiveUserId = input.ExecutiveUserId,
            ModalityCode = input.ModalityCode.Trim().ToUpperInvariant(),
            OriginCountryCode = input.OriginCountryCode?.ToUpperInvariant(),
            DestinationCountryCode = input.DestinationCountryCode?.ToUpperInvariant(),
            Priority = input.Priority,
            ComplexityPoints = input.ComplexityPoints,
            Status = "DRAFT",
            CreatedBy = input.ActorUserId
        };
        var openingStage = new OperationsCaseStage { CaseId = operation.Id, DepartmentCode = "APERTURAS" };
        operation.Stages.Add(openingStage);
        db.Cases.Add(operation);
        OperationsAuditWriter.Case(db, operation.Id, openingStage.Id, input.ActorUserId,
            "operations.case.created", null, new {
                operation.Idtra, operation.CompanyId, operation.AccountId, operation.ModalityCode
            });
        Enqueue("operations.case.created.v1", new { caseId = operation.Id, idtra, actorId = input.ActorUserId, version = operation.Version });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CaseCreatedResult(operation.Id, idtra, operation.Status, openingStage.Id);
    }

    public async Task<StageAssignedResult?> AssignStageAsync(AssignStageCommand input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var stage = await db.CaseStages.SingleOrDefaultAsync(
            x => x.Id == input.StageId && x.CaseId == input.CaseId, ct);
        var operation = await db.Cases.SingleOrDefaultAsync(x => x.Id == input.CaseId, ct);
        if (stage is null || operation is null || stage.Status is "COMPLETED" or "SKIPPED")
            return null;

        // Serialize assignments for this department: no simultaneous stale workload calculations.
        var lockKey = operation.CompanyId.ToString("N") + ":" + stage.DepartmentCode;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);

        if (await db.StageAssignments.AnyAsync(x => x.CaseStageId == stage.Id && x.ReleasedAtUtc == null, ct))
            return null;

        // Routing predicates always derive from persisted case data, not caller-supplied assign parameters.
        var origin = operation.OriginCountryCode;
        var destination = operation.DestinationCountryCode;
        var rules = await db.AssignmentRules.AsNoTracking()
            .Include(x => x.Team).ThenInclude(x => x.Members)
            .Where(x => x.IsActive && x.Team.IsActive && x.Team.CompanyId == operation.CompanyId &&
                x.DepartmentCode == stage.DepartmentCode &&
                (x.ModalityCode == null || x.ModalityCode == operation.ModalityCode) &&
                (x.OriginCountryCode == null || x.OriginCountryCode == origin) &&
                (x.DestinationCountryCode == null || x.DestinationCountryCode == destination))
            .ToListAsync(ct);

        var load = await db.StageAssignments.AsNoTracking()
            .Where(x => x.ReleasedAtUtc == null)
            .GroupBy(x => x.UserId).Select(g => new { UserId = g.Key, Points = g.Sum(x => x.AssignedPoints) })
            .ToDictionaryAsync(x => x.UserId, x => x.Points, ct);

        var candidates = rules.SelectMany(rule => rule.Team.Members
                .Where(member => member.IsAvailable && member.CapacityPoints > 0)
                .Select(member => new
                {
                    rule.TeamId, member.UserId, rule.Priority,
                    RelativeLoad = (load.GetValueOrDefault(member.UserId) + operation.ComplexityPoints)
                        / member.CapacityPoints
                }))
            .OrderBy(x => x.RelativeLoad).ThenByDescending(x => x.Priority)
            .ThenBy(x => x.UserId).ThenBy(x => x.TeamId).ToList();

        if (candidates.Count == 0) return null;
        var selected = candidates[0];
        var assignment = new OperationsStageAssignment
        {
            CaseStageId = stage.Id, TeamId = selected.TeamId, UserId = selected.UserId,
            AssignedPoints = operation.ComplexityPoints, AssignedBy = input.ActorUserId
        };
        stage.TeamId = selected.TeamId;
        stage.Status = "READY";
        stage.Version++;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        operation.Version++;
        db.StageAssignments.Add(assignment);
        OperationsAuditWriter.Case(db, operation.Id, stage.Id, input.ActorUserId,
            "operations.stage.assigned", null, new {
                assignment.TeamId, assignment.UserId, stage.DepartmentCode
            });
        Enqueue("operations.case.assigned.v1",
            new { caseId = operation.Id, stageId = stage.Id, actorId = input.ActorUserId, version = operation.Version });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new StageAssignedResult(assignment.Id, selected.TeamId, selected.UserId, selected.RelativeLoad);
    }

    public async Task<DocumentRegisteredResult> RegisterDocumentAsync(RegisterDocumentCommand input, CancellationToken ct)
    {
        var operationExists = await db.Cases.AnyAsync(x => x.Id == input.CaseId, ct);
        if (!operationExists) throw new KeyNotFoundException("Operations case not found.");
        var document = new OperationsDocument
        {
            CaseId = input.CaseId, DocumentTypeCode = input.DocumentTypeCode.Trim().ToUpperInvariant()
        };
        var version = new OperationsDocumentVersion
        {
            DocumentId = document.Id, VersionNumber = 1, StorageFileId = input.StorageFileId,
            FileName = input.FileName, MimeType = input.MimeType,
            Sha256 = input.Sha256, UploadedBy = input.ActorUserId
        };
        document.Versions.Add(version);
        db.Documents.Add(document);
        OperationsAuditWriter.Case(db, input.CaseId, null, input.ActorUserId,
            "operations.document.registered", null,
            new { documentId = document.Id, versionId = version.Id, input.DocumentTypeCode });
        Enqueue("operations.document.registered.v1", new { caseId = input.CaseId, documentId = document.Id, versionId = version.Id });
        await db.SaveChangesAsync(ct);
        return new DocumentRegisteredResult(document.Id, version.Id);
    }

    public async Task<DocumentExtractionQueuedResult> QueueDocumentExtractionAsync(QueueDocumentExtractionCommand input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var doc = await db.DocumentVersions.Where(x => x.Id == input.DocumentVersionId)
            .Select(x => new { x.Id, x.StorageFileId, x.Document.CaseId }).SingleOrDefaultAsync(ct);
        if (doc is null || doc.CaseId != input.CaseId)
            throw new KeyNotFoundException("Document version not found in case.");

        var lockKey = "operations-extraction:" + doc.Id.ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
        var existing = await db.ExtractionJobs.AsNoTracking()
            .Where(x => x.DocumentVersionId == doc.Id && (x.Status == "QUEUED" || x.Status == "PROCESSING" || x.Status == "REVIEW_REQUIRED"))
            .OrderByDescending(x => x.RequestedAtUtc).FirstOrDefaultAsync(ct);
        if (existing is not null)
            return new DocumentExtractionQueuedResult(existing.Id, existing.Status);

        var job = new OperationsDocumentExtractionJob
        {
            DocumentVersionId = doc.Id, RequestedBy = input.ActorUserId
        };
        db.ExtractionJobs.Add(job);
        OperationsAuditWriter.Case(db, input.CaseId, null, input.ActorUserId,
            "operations.document.extraction.queued", null,
            new { jobId = job.Id, documentVersionId = doc.Id });
        // DataExtraction/AI consumer contract is a separate integration: never invent a successful analysis.
        Enqueue("operations.document.extraction.requested.v1",
            new { caseId = input.CaseId, documentVersionId = doc.Id, storageFileId = doc.StorageFileId, jobId = job.Id });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DocumentExtractionQueuedResult(job.Id, job.Status);
    }

    private async Task<long> NextSequenceAsync(string seriesCode, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO operations.operations_case_sequences (id, series_code, last_number, updated_at_utc)
            VALUES (@id, @series, 1, now())
            ON CONFLICT (series_code)
            DO UPDATE SET last_number = operations_case_sequences.last_number + 1, updated_at_utc = now()
            RETURNING last_number;
            """, connection, (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("series", seriesCode);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private void Enqueue(string name, object payload) =>
        db.OutboxMessages.Add(OperationsOutboxFactory.Create(name, JsonSerializer.Serialize(payload)));
}
