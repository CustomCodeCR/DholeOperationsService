using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Api.Integrations;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// Read-only PostgreSQL-backed completeness projection; never validates,
/// changes ownership, assigns a team, or rewrites an accepted QUO.
/// </summary>
public static class OperationsApertureCompletenessEndpoints
{
    public sealed record PreviewRequest(string Modality, Guid AccountId, JsonElement OpeningData, Guid? CaseId = null);

    public static IEndpointRouteBuilder MapOperationsApertureCompletenessEndpoints(
        this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();
        // Pure preview; no persistence, assignment or commercial mutation.
        api.MapPost("/apertures/drafts/completeness", async (PreviewRequest input, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, OperationsCrmAccountResolver crm, CancellationToken ct) =>
        {
            var accountId = input.AccountId;
            var manual = true;
            Guid? executive = null;
            if (input.CaseId is Guid caseId)
            {
                if (!await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
                var record = await db.Cases.AsNoTracking().Where(x => x.Id == caseId)
                    .Select(x => new { x.AccountId, x.ApertureType, x.ExecutiveUserId }).SingleOrDefaultAsync(ct);
                if (record is null) return Results.NotFound();
                accountId = record.AccountId;
                manual = record.ApertureType == "MANUAL";
                executive = record.ExecutiveUserId;
            }
            else if (!OperationsCaseAccess.HasScope(http.User, "operations.case.create")) return Results.Forbid();
            if (input.OpeningData.ValueKind != JsonValueKind.Object ||
                input.OpeningData.GetRawText().Length > 131072)
                return Results.BadRequest(new { code = "OPERATIONS_OPENING_DATA_INVALID" });
            if (input.CaseId is null && accountId != Guid.Empty)
            {
                var result = await crm.ResolveAsync(accountId, http.Request.Headers.Authorization.ToString(), ct);
                if (result.ErrorCode is not null && result.StatusCode >= 500)
                    return Results.Json(new { code = result.ErrorCode }, statusCode: result.StatusCode);
                executive = result.ExecutiveUserId;
            }
            var fields = OperationsOpeningDraftValidator.EvaluateCase(input.Modality,
                input.OpeningData.GetRawText(), accountId, manual).ToList();
            fields.Add(new("executiveUserId", executive.HasValue && executive.Value != Guid.Empty ? "CONFIRMADO" : "PENDIENTE_BLOQUEANTE", "CRM determines the responsible executive"));
            var missingFields = fields.Where(x => x.CompletionStatus == "PENDIENTE_BLOQUEANTE")
                .Select(x => x.FieldCode).ToArray();
            return Results.Ok(new { fields, missingFields, canPassFieldValidation = missingFields.Length == 0 });
        });
        api.MapGet("/cases/{id:guid}/aperture/completeness", async (
            Guid id, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var record = await db.Cases.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.Id, x.ApertureType, x.ModalityCode,
                    x.OpeningDataJson, x.Status, x.Version, x.AccountId, x.ExecutiveUserId }).SingleOrDefaultAsync(ct);
            if (record is null) return Results.NotFound();
            var fields = OperationsOpeningDraftValidator.EvaluateCase(
                record.ModalityCode, record.OpeningDataJson, record.AccountId, record.ApertureType == "MANUAL").ToList();
            fields.Add(new("executiveUserId", record.ExecutiveUserId != Guid.Empty ? "CONFIRMADO" : "PENDIENTE_BLOQUEANTE", "CRM determines the responsible executive"));
            var missingFields = fields.Where(x => x.CompletionStatus == "PENDIENTE_BLOQUEANTE")
                .Select(x => x.FieldCode).ToArray();
            // This preview does not imply an available two-person validation pool.
            return Results.Ok(new { caseId = record.Id, record.ApertureType,
                record.Status, record.Version, fields, missingFields,
                canPassFieldValidation = missingFields.Length == 0 });
        });
        api.MapGet("/cases/{id:guid}/aperture/draft", async (
            Guid id, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var record = await db.Cases.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.Id, x.Status, x.Version, x.OpeningDataJson,
                    x.UpdatedAtUtc, x.ModalityCode, x.Priority, x.ComplexityPoints,
                    x.OriginCountryCode, x.DestinationCountryCode })
                .SingleOrDefaultAsync(ct);
            return record is null ? Results.NotFound() : Results.Ok(record);
        });
        return app;
    }
}
