using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// Tenant- and user-scoped PostgreSQL drafts, without IDTRA allocation or team
/// requirements. In-flight multi-tab changes use optimistic concurrency.
/// </summary>
public static class OperationsManualOpeningDraftEndpoints
{
    public sealed record SaveDraftRequest(long ExpectedVersion, JsonElement Draft);
    private const int MaxJsonLength = 131072;

    public static IEndpointRouteBuilder MapOperationsManualOpeningDraftEndpoints(
        this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations/apertures/drafts").RequireAuthorization();

        api.MapGet("/current", async (
            HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            CancellationToken ct) =>
        {
            var identity = await ResolveIdentityAsync(http, access, ct);
            if (identity is null) return Results.Forbid();
            var (companyId, userId) = identity.Value;
            var record = await db.ManualOpeningDrafts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.CompanyId == companyId &&
                    x.CreatedBy == userId, ct);
            if (record is null) return Results.NoContent();
            using var document = JsonDocument.Parse(record.PayloadJson);
            return Results.Ok(new {
                record.Id, record.Version, record.CreatedAtUtc, record.UpdatedAtUtc,
                draft = document.RootElement.Clone()
            });
        });

        api.MapPut("/current", async (
            SaveDraftRequest input, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var identity = await ResolveIdentityAsync(http, access, ct);
            if (identity is null) return Results.Forbid();
            if (input.ExpectedVersion < 0 ||
                input.Draft.ValueKind != JsonValueKind.Object ||
                input.Draft.GetRawText().Length > MaxJsonLength)
                return Results.BadRequest(new { code = "OPERATIONS_DRAFT_INVALID" });
            var (companyId, userId) = identity.Value;
            var payload = input.Draft.GetRawText();

            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, ct);
            var lockKey = "operations-manual-draft:" + companyId.ToString("N") +
                ":" + userId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
            var record = await db.ManualOpeningDrafts
                .SingleOrDefaultAsync(x => x.CompanyId == companyId &&
                    x.CreatedBy == userId, ct);
            var version = record?.Version ?? 0L;
            if (version != input.ExpectedVersion)
                return Results.Conflict(new {
                    code = "OPERATIONS_DRAFT_VERSION_CONFLICT",
                    currentVersion = version
                });
            if (record is null)
            {
                record = new OperationsManualOpeningDraft {
                    CompanyId = companyId, CreatedBy = userId
                };
                db.ManualOpeningDrafts.Add(record);
            }
            else
                record.Version++;
            record.PayloadJson = payload;
            record.UpdatedAtUtc = DateTimeOffset.UtcNow;
            // Metadata-only event. Never publish contact details, customer notes
            // or the draft payload into a shared realtime channel.
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.aperture.draft-saved.v1",
                JsonSerializer.Serialize(new { draftId = record.Id,
                    userId, version = record.Version })));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new {
                record.Id, record.Version, record.UpdatedAtUtc
            });
        });

        api.MapDelete("/current", async (
            HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            CancellationToken ct) =>
        {
            var identity = await ResolveIdentityAsync(http, access, ct);
            if (identity is null) return Results.Forbid();
            var (companyId, userId) = identity.Value;
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, ct);
            var lockKey = "operations-manual-draft:" + companyId.ToString("N") +
                ":" + userId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
            var record = await db.ManualOpeningDrafts
                .SingleOrDefaultAsync(x => x.CompanyId == companyId &&
                    x.CreatedBy == userId, ct);
            if (record is not null) db.ManualOpeningDrafts.Remove(record);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    private static async Task<(Guid CompanyId, Guid UserId)?> ResolveIdentityAsync(
        HttpContext http, OperationsCaseAccess access, CancellationToken ct)
    {
        if (!OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
            return null;
        var userId = OperationsCaseAccess.UserId(http.User);
        var companyId = await access.ResolveCompanyIdAsync(http.User, ct);
        return userId is null || companyId is null || companyId == Guid.Empty
            ? null : (companyId.Value, userId.Value);
    }
}
