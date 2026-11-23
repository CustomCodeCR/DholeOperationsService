using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

public static class OperationsOpeningDraftEndpoints
{
    public sealed record SaveDraftRequest(JsonElement Payload);

    public static IEndpointRouteBuilder MapOperationsOpeningDraftEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operations/apertures/drafts").RequireAuthorization();

        group.MapGet("", async (HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var identity = await ResolveAsync(http, access, ct);
            if (identity is null) return Results.Forbid();
            var (company, user) = identity.Value;
            var items = await db.OpeningDrafts.AsNoTracking()
                .Where(x => x.CompanyId == company && x.UserId == user)
                .OrderByDescending(x => x.UpdatedAtUtc).Take(50)
                .Select(x => new { x.Id, x.CreatedAtUtc, x.UpdatedAtUtc })
                .ToArrayAsync(ct);
            return Results.Ok(new { items });
        });

        group.MapGet("/{draftId:guid}", async (Guid draftId, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var identity = await ResolveAsync(http, access, ct);
            if (identity is null) return Results.Forbid();
            var (company, user) = identity.Value;
            var draft = await db.OpeningDrafts.AsNoTracking()
                .Where(x => x.Id == draftId && x.CompanyId == company && x.UserId == user)
                .Select(x => new { x.Id, x.PayloadJson, x.UpdatedAtUtc })
                .FirstOrDefaultAsync(ct);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        });

        // Creates/updates with a client-generated UUID. Retries cannot create duplicates;
        // no assigned team, CRM account or validated field is required for an incomplete draft.
        group.MapPut("/{draftId:guid}", async (Guid draftId, SaveDraftRequest input,
            HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var identity = await ResolveAsync(http, access, ct);
            if (identity is null) return Results.Forbid();
            if (draftId == Guid.Empty || input.Payload.ValueKind != JsonValueKind.Object ||
                input.Payload.GetRawText().Length > 65536)
                return Results.BadRequest(new { code = "OPERATIONS_DRAFT_INVALID" });
            var (company, user) = identity.Value;
            var draft = await db.OpeningDrafts.FirstOrDefaultAsync(x => x.Id == draftId, ct);
            if (draft is not null && (draft.CompanyId != company || draft.UserId != user))
                return Results.NotFound(); // no cross-tenant/user mutation
            if (draft is null)
            {
                draft = new OperationsOpeningDraft { Id = draftId, CompanyId = company, UserId = user };
                db.OpeningDrafts.Add(draft);
            }
            draft.PayloadJson = input.Payload.GetRawText();
            draft.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { draft.Id, draft.UpdatedAtUtc });
        });

        group.MapDelete("/{draftId:guid}", async (Guid draftId, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var identity = await ResolveAsync(http, access, ct);
            if (identity is null) return Results.Forbid();
            var (company, user) = identity.Value;
            var draft = await db.OpeningDrafts.FirstOrDefaultAsync(x =>
                x.Id == draftId && x.CompanyId == company && x.UserId == user, ct);
            if (draft is null) return Results.NoContent();
            db.OpeningDrafts.Remove(draft);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        return app;
    }

    private static async Task<(Guid Company, Guid User)?> ResolveAsync(
        HttpContext http, OperationsCaseAccess access, CancellationToken ct)
    {
        if (!OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
            return null;
        var user = OperationsCaseAccess.UserId(http.User);
        var company = await access.ResolveCompanyIdAsync(http.User, ct);
        return user.HasValue && user.Value != Guid.Empty && company.HasValue && company.Value != Guid.Empty
            ? (company.Value, user.Value) : null;
    }
}
