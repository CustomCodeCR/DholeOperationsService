using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>Owner-scoped PostgreSQL wizard drafts, including drafts missing client/team/route.</summary>
public static class OperationsOpeningAutosaveEndpoints
{
    public sealed record SaveDraftRequest(JsonElement Data, Guid? CompanyId = null);

    public static IEndpointRouteBuilder MapOperationsOpeningAutosaveEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations/apertures/drafts").RequireAuthorization();

        api.MapGet("", async (HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            var rows = await db.Set<OperationsOpeningAutosaveDraft>().AsNoTracking()
                .Where(x => x.OwnerUserId == actor.Value)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Select(x => new { x.Id, x.CompanyId, x.UpdatedAtUtc })
                .Take(50).ToArrayAsync(ct);
            return Results.Ok(rows);
        });

        api.MapGet("/{id:guid}", async (Guid id, HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            var draft = await db.Set<OperationsOpeningAutosaveDraft>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == actor.Value, ct);
            return draft is null ? Results.NotFound() :
                Results.Ok(new { draft.Id, draft.CompanyId, draft.UpdatedAtUtc, data = JsonSerializer.Deserialize<JsonElement>(draft.PayloadJson) });
        });

        api.MapPut("/{id:guid}", async (Guid id, SaveDraftRequest request, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            if (id == Guid.Empty || request.Data.ValueKind != JsonValueKind.Object ||
                request.Data.GetRawText().Length > 131072)
                return Results.BadRequest(new { code = "OPERATIONS_DRAFT_INVALID" });
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            if (request.CompanyId is { } requested && requested != Guid.Empty &&
                !OperationsCaseAccess.IsSystemAdministrator(http.User) && company != requested)
                return Results.Forbid();

            var existing = await db.Set<OperationsOpeningAutosaveDraft>()
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (existing is not null && existing.OwnerUserId != actor.Value)
                return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            if (existing is null)
            {
                existing = new OperationsOpeningAutosaveDraft { Id = id, OwnerUserId = actor.Value };
                db.Set<OperationsOpeningAutosaveDraft>().Add(existing);
            }
            existing.CompanyId = request.CompanyId is { } selected && selected != Guid.Empty
                ? selected : company;
            existing.PayloadJson = request.Data.GetRawText();
            existing.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { existing.Id, existing.UpdatedAtUtc });
        });

        api.MapDelete("/{id:guid}", async (Guid id, HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            await db.Set<OperationsOpeningAutosaveDraft>()
                .Where(x => x.Id == id && x.OwnerUserId == actor.Value)
                .ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });
        return app;
    }
}
