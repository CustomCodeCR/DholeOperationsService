using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>Protected stage-owned documents. Legacy rows remain intact but
/// cannot enter the transversal projection until ownership is verified.</summary>
public static class OperationsDocumentEndpoints
{
    public sealed record RegisterRequest(Guid OwnerStageId, Guid StorageFileId, string DocumentTypeCode, long ExpectedVersion);
    private sealed record StorageMetadata(Guid Id, string FileName, string MimeType, string? Sha256, long SizeInBytes);

    private static bool Enabled(IConfiguration config) =>
        bool.TryParse(config["OperationsDocuments:Enabled"], out var enabled) && enabled;

    private static async Task<bool> CanUpload(Guid stageId, HttpContext http, OperationsCaseAccess access,
        OperationsDbContext db, CancellationToken ct)
    {
        if (!OperationsCaseAccess.HasScope(http.User, "operations.document.upload")) return false;
        var stage = await db.CaseStages.AsNoTracking().Where(s => s.Id == stageId)
            .Select(s => new { s.CaseId, s.DepartmentCode, s.Status, CaseStatus = s.Case.Status }).SingleOrDefaultAsync(ct);
        return stage is not null && OperationsDocumentPolicy.CanRegister(stage.CaseId, stage.CaseId,
            stage.DepartmentCode, stage.Status, stage.CaseStatus, "HBL") &&
            await access.CanEditAssignedStageAsync(stageId, http.User, ct);
    }

    // Filter is built from persisted document ownership, not a caller's department.
    private static IQueryable<OperationsDocument> Transversal(OperationsDbContext db, Guid caseId) =>
        db.Documents.AsNoTracking().Where(d => d.CaseId == caseId && d.OwnerStageId != null &&
            d.OwnerStage != null && d.OwnerStage.CaseId == caseId && d.AccessClass == "OPERATIONAL" &&
            (d.DocumentTypeCode == "HBL" || d.DocumentTypeCode == "MBL" ||
             d.DocumentTypeCode == "BOOKING" || d.DocumentTypeCode == "SHIPPING_ORDER") &&
            (d.OwnerStage.DepartmentCode == "APERTURAS" || d.OwnerStage.DepartmentCode == "COORDINACION_ORIGEN" ||
             d.OwnerStage.DepartmentCode == "TRANSITO" || d.OwnerStage.DepartmentCode == "PANAMA" ||
             d.OwnerStage.DepartmentCode == "COSTA_RICA"));

    public static IEndpointRouteBuilder MapOperationsDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();
        api.MapGet("/document-stages/{stageId:guid}/upload-access", async (
            Guid stageId, HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            IConfiguration config, CancellationToken ct) =>
            Enabled(config) && await CanUpload(stageId, http, access, db, ct)
                ? Results.NoContent() : Results.StatusCode(403));

        api.MapGet("/document-stages/{stageId:guid}/files/{fileId:guid}/read-access", async (
            Guid stageId, Guid fileId, HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            IConfiguration config, CancellationToken ct) =>
        {
            if (!Enabled(config) || fileId == Guid.Empty) return Results.StatusCode(403);
            var caseId = await db.CaseStages.AsNoTracking().Where(s => s.Id == stageId)
                .Select(s => (Guid?)s.CaseId).SingleOrDefaultAsync(ct);
            if (!caseId.HasValue || !await access.CanReadAsync(caseId.Value, http.User, ct))
                return Results.StatusCode(403);
            return await Transversal(db, caseId.Value).AnyAsync(d =>
                d.OwnerStageId == stageId && d.Versions.Any(v => v.StorageFileId == fileId), ct)
                ? Results.NoContent() : Results.StatusCode(403);
        });

        api.MapGet("/cases/{id:guid}/documents/v2", async (
            Guid id, HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            IConfiguration config, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            if (!Enabled(config)) return Results.Ok(new { caseId = id, enabled = false, items = Array.Empty<object>() });
            var items = await Transversal(db, id).OrderBy(d => d.CreatedAtUtc)
                .Select(d => new { d.Id, d.DocumentTypeCode, d.OwnerStageId, departmentCode = d.OwnerStage!.DepartmentCode,
                    d.Status, d.Version, d.CreatedAtUtc, versions = d.Versions.OrderByDescending(v => v.VersionNumber)
                        .Select(v => new { v.Id, v.VersionNumber, v.StorageFileId, v.FileName, v.MimeType, v.UploadedBy,
                            v.UploadedAtUtc, v.ReviewStatus }).ToArray() }).ToArrayAsync(ct);
            return Results.Ok(new { caseId = id, enabled = true, items });
        });

        api.MapPost("/cases/{id:guid}/documents/v2", async (
            Guid id, RegisterRequest input, HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            IConfiguration config, IHttpClientFactory factory, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (!Enabled(config) || actor is null || !await access.CanReadAsync(id, http.User, ct) ||
                !await CanUpload(input.OwnerStageId, http, access, db, ct)) return Results.Forbid();
            var type = OperationsDocumentPolicy.NormalizeType(input.DocumentTypeCode);
            if (type is null || input.StorageFileId == Guid.Empty || input.ExpectedVersion < 1)
                return Results.BadRequest(new { code = "OPERATIONS_DOCUMENT_INVALID" });
            if (!await db.CaseStages.AsNoTracking().AnyAsync(s => s.Id == input.OwnerStageId && s.CaseId == id, ct))
                return Results.Forbid();
            var metadata = await ReadStorageMetadata(input, http, factory, config, ct);
            if (metadata is null) return Results.Conflict(new { code = "OPERATIONS_DOCUMENT_STORAGE_UNVERIFIED" });

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var key = "operations-document:" + input.StorageFileId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
            if (!await CanUpload(input.OwnerStageId, http, access, db, ct)) return Results.Forbid();
            var previous = await db.DocumentVersions.AsNoTracking().Where(v => v.StorageFileId == input.StorageFileId)
                .Select(v => new { v.Id, v.DocumentId, v.Document.CaseId, v.Document.OwnerStageId,
                    v.Document.DocumentTypeCode, v.Document.AccessClass }).SingleOrDefaultAsync(ct);
            if (previous is not null)
            {
                if (previous.CaseId != id || previous.OwnerStageId != input.OwnerStageId ||
                    previous.DocumentTypeCode != type || previous.AccessClass != OperationsDocumentPolicy.Operational)
                    return Results.Conflict(new { code = "OPERATIONS_DOCUMENT_ALREADY_REGISTERED" });
                await tx.CommitAsync(ct);
                return Results.Ok(new { documentId = previous.DocumentId, versionId = previous.Id, duplicate = true });
            }
            var operation = await db.Cases.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (operation is null || operation.Status is "COMPLETED" or "CANCELLED") return Results.Forbid();
            if (operation.Version != input.ExpectedVersion)
                return Results.Conflict(new { code = "OPERATIONS_CASE_VERSION_CONFLICT", currentVersion = operation.Version });
            var doc = new OperationsDocument { CaseId = id, OwnerStageId = input.OwnerStageId,
                AccessClass = OperationsDocumentPolicy.Operational, DocumentTypeCode = type };
            var version = new OperationsDocumentVersion { DocumentId = doc.Id, StorageFileId = metadata.Id,
                VersionNumber = 1, FileName = metadata.FileName, MimeType = metadata.MimeType,
                Sha256 = metadata.Sha256, UploadedBy = actor.Value };
            doc.Versions.Add(version);
            db.Documents.Add(doc);
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, id, input.OwnerStageId, actor.Value, "operations.document.registered", null,
                new { documentId = doc.Id, versionId = version.Id, documentTypeCode = type });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.document.registered.v1",
                JsonSerializer.Serialize(new { caseId = id, documentId = doc.Id, versionId = version.Id })));
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.case.updated.v1",
                JsonSerializer.Serialize(new { caseId = id, version = operation.Version })));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "OPERATIONS_CASE_VERSION_CONFLICT" }); }
            await tx.CommitAsync(ct);
            return Results.Created($"/api/operations/cases/{id}/documents/v2", new { documentId = doc.Id, versionId = version.Id, duplicate = false });
        });

        return app;
    }

    private static async Task<StorageMetadata?> ReadStorageMetadata(RegisterRequest input, HttpContext http,
        IHttpClientFactory factory, IConfiguration config, CancellationToken ct)
    {
        if (!Uri.TryCreate(config["OperationsDocuments:StorageBaseUrl"], UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) ||
            !AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization.ToString(), out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(bearer.Parameter)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/"),
            $"api/v1/storage/operations-documents/{input.OwnerStageId:D}/files/{input.StorageFileId:D}/registration"));
        request.Headers.Authorization = bearer;
        try
        {
            using var response = await factory.CreateClient("operations-document-storage").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var value = await response.Content.ReadFromJsonAsync<StorageMetadata>(cancellationToken: ct);
            return value is not null && value.Id == input.StorageFileId && value.SizeInBytes > 0 &&
                !string.IsNullOrWhiteSpace(value.FileName) && value.FileName.Length <= 255 &&
                value.MimeType == "application/pdf" && value.Sha256 is { Length: 64 } &&
                value.Sha256.All(Uri.IsHexDigit) ? value : null;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
