using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>User-scoped PostgreSQL autosave, including drafts without CRM account/team/route.</summary>
public static class OperationsManualDraftEndpoints
{
    public sealed record SaveDraftRequest(JsonElement State);

    public static IEndpointRouteBuilder MapOperationsManualDraftEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations/apertures/drafts").RequireAuthorization();

        api.MapGet("/latest", async (HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            var userId = OperationsCaseAccess.UserId(http.User);
            if (userId is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            var draft = await db.ManualOpeningDrafts.AsNoTracking()
                .Where(x => x.UserId == userId.Value)
                .OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id)
                .Select(x => new { x.Id, x.PayloadJson, x.UpdatedAtUtc })
                .FirstOrDefaultAsync(ct);
            return draft is null ? Results.NoContent() : Results.Ok(draft);
        });

        api.MapPut("/{draftId:guid}", async (
            Guid draftId, SaveDraftRequest body, HttpContext http,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var userId = OperationsCaseAccess.UserId(http.User);
            if (userId is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            if (draftId == Guid.Empty || body.State.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { code = "OPERATIONS_DRAFT_INVALID" });
            var payload = body.State.GetRawText();
            if (System.Text.Encoding.UTF8.GetByteCount(payload) > 65536)
                return Results.BadRequest(new { code = "OPERATIONS_DRAFT_TOO_LARGE" });
            var draft = await db.ManualOpeningDrafts.SingleOrDefaultAsync(x => x.Id == draftId, ct);
            if (draft is not null && draft.UserId != userId.Value) return Results.NotFound();
            if (draft is null)
            {
                draft = new OperationsManualOpeningDraft { Id = draftId, UserId = userId.Value };
                db.ManualOpeningDrafts.Add(draft);
            }
            draft.PayloadJson = payload;
            draft.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { draft.Id, draft.UpdatedAtUtc });
        });

        api.MapDelete("/{draftId:guid}", async (
            Guid draftId, HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            var userId = OperationsCaseAccess.UserId(http.User);
            if (userId is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            var draft = await db.ManualOpeningDrafts
                .SingleOrDefaultAsync(x => x.Id == draftId && x.UserId == userId.Value, ct);
            if (draft is not null)
            {
                db.ManualOpeningDrafts.Remove(draft);
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });
        return app;
    }
}
