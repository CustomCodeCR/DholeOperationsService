using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>Persist incomplete, per-operator opening wizards without allocating an IDTRA.</summary>
public static class OperationsApertureDraftEndpoints
{
    public sealed record SaveDraftRequest(JsonElement Payload, long? ExpectedVersion = null);
    public static IEndpointRouteBuilder MapOperationsApertureDraftEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations/aperture-drafts").RequireAuthorization();

        api.MapGet("/latest", async (HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            if (company is null) return Results.Conflict(new { code = "OPERATIONS_DRAFT_COMPANY_REQUIRED" });
            var item = await db.Set<OperationsApertureDraft>().AsNoTracking()
                .Where(x => x.CompanyId == company && x.OwnerUserId == actor.Value)
                .OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(ct);
            return item is null ? Results.NoContent() : Results.Ok(ToResult(item));
        });

        api.MapPost("/", async (SaveDraftRequest request, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            if (company is null) return Results.Conflict(new { code = "OPERATIONS_DRAFT_COMPANY_REQUIRED" });
            if (!Valid(request.Payload)) return Results.BadRequest(new { code = "OPERATIONS_DRAFT_INVALID" });
            var record = new OperationsApertureDraft {
                CompanyId = company.Value, OwnerUserId = actor.Value,
                PayloadJson = request.Payload.GetRawText()
            };
            db.Set<OperationsApertureDraft>().Add(record);
            await db.SaveChangesAsync(ct);
            return Results.Created("/api/operations/aperture-drafts/" + record.Id, ToResult(record));
        });

        api.MapPatch("/{id:guid}", async (Guid id, SaveDraftRequest request,
            HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            if (company is null) return Results.Forbid();
            if (!Valid(request.Payload) || request.ExpectedVersion is null or < 1)
                return Results.BadRequest(new { code = "OPERATIONS_DRAFT_INVALID" });
            var item = await db.Set<OperationsApertureDraft>()
                .SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == company &&
                    x.OwnerUserId == actor.Value, ct);
            if (item is null) return Results.NotFound();
            if (item.Version != request.ExpectedVersion)
                return Results.Conflict(new { code = "OPERATIONS_DRAFT_VERSION_CONFLICT", item.Version });
            item.PayloadJson = request.Payload.GetRawText();
            item.Version++;
            item.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResult(item));
        });

        api.MapDelete("/{id:guid}", async (Guid id, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            if (company is null) return Results.Forbid();
            var item = await db.Set<OperationsApertureDraft>()
                .SingleOrDefaultAsync(x => x.Id == id && x.CompanyId == company &&
                    x.OwnerUserId == actor.Value, ct);
            if (item is null) return Results.NoContent();
            db.Set<OperationsApertureDraft>().Remove(item);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    private static bool Valid(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.GetRawText().Length <= 65536;

    private static object ToResult(OperationsApertureDraft item) => new {
        item.Id, item.CompanyId, item.OwnerUserId,
        item.Version, item.UpdatedAtUtc, item.PayloadJson
    };
}
