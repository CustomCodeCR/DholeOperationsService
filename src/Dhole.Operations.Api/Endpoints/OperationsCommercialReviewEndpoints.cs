using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// Commercial review belongs to Pricing. Operations stores request state and
/// displays an explicit yellow warning, never changes an accepted QUO price.
/// </summary>
public static class OperationsCommercialReviewEndpoints
{
    public sealed record AddServiceRequest(
        Guid IdempotencyKey, string ServiceCode, string ServiceName, string Reason);

    public sealed record LinkQuoteRequest(Guid RateId, long ExpectedVersion);

    public sealed record RequestDiscrepancyReview(long ExpectedVersion, string Fingerprint, string? Comment);

    public static IEndpointRouteBuilder MapOperationsCommercialReviewEndpoints(
        this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();

        // The same command is used by both modules; no client-side dual write.
        api.MapPost("/cases/{caseId:guid}/quote-links", async (
            Guid caseId, LinkQuoteRequest input, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "pricing.rate.update") ||
                !(OperationsCaseAccess.HasScope(http.User, "operations.case.update") ||
                  OperationsCaseAccess.HasScope(http.User, "operations.origin.update") ||
                  OperationsCaseAccess.HasScope(http.User, "operations.aperture.update")) ||
                !await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            if (input.RateId == Guid.Empty) return Results.BadRequest();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var key = "operations-quote-link:" + caseId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
            var operation = await db.Cases.SingleOrDefaultAsync(x => x.Id == caseId, ct);
            if (operation is null) return Results.NotFound();
            if (operation.ApertureType != "MANUAL" || operation.Status is "CANCELLED" or "COMPLETED")
                return Results.Conflict(new { code = "MANUAL_ACTIVE_IDTRA_REQUIRED" });
            if (operation.SourceQuoteOptionId == input.RateId)
                return Results.Ok(new { status = "ACCEPTED", duplicate = true });
            if (operation.SourceQuoteId.HasValue)
                return Results.Conflict(new { code = "IDTRA_ALREADY_LINKED" });
            var pending = await db.CaseQuoteLinks.SingleOrDefaultAsync(x => x.CaseId == caseId && x.Status == "PENDING_LINK", ct);
            if (pending is not null)
                return pending.QuoteOptionId == input.RateId
                    ? Results.Ok(new { requestId = pending.Id, status = pending.Status, duplicate = true })
                    : Results.Conflict(new { code = "QUOTE_LINK_IN_PROGRESS" });
            if (operation.Version != input.ExpectedVersion)
                return Results.Conflict(new { code = "OPERATIONS_CASE_VERSION_CONFLICT" });
            var siblings = await db.Cases.AsNoTracking().Where(x => x.SourceQuoteOptionId == input.RateId &&
                x.CompanyId == operation.CompanyId && x.AccountId == operation.AccountId)
                .Select(x => x.Idtra).Distinct().Take(30).ToArrayAsync(ct);
            var link = new OperationsCaseQuoteLink
            {
                CaseId = caseId, QuoteId = input.RateId, QuoteOptionId = input.RateId,
                IsPrimary = false, Status = "PENDING_LINK", LinkedBy = actor.Value
            };
            db.CaseQuoteLinks.Add(link);
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, caseId, null, actor.Value, "operations.quote-link.requested", null,
                new { requestId = link.Id, input.RateId, operation.Idtra });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.quote-link.requested.v1",
                JsonSerializer.Serialize(new { requestId = link.Id, caseId, operation.AccountId, operation.CompanyId,
                    rateId = input.RateId, requestedBy = actor.Value, operation.Idtra, operation.ModalityCode, existingIdtras = siblings })));
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.case.updated.v1",
                JsonSerializer.Serialize(new { caseId, version = operation.Version })));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "OPERATIONS_CASE_VERSION_CONFLICT" }); }
            await tx.CommitAsync(ct);
            return Results.Accepted($"/api/operations/cases/{caseId}/workspace", new { requestId = link.Id, status = link.Status, duplicate = false });
        });

        api.MapGet("/cases/{caseId:guid}/commercial/comparison", async (
            Guid caseId, HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            var operation = await db.Cases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId, ct);
            if (operation is null) return Results.NotFound();
            var snapshot = await db.AcceptedQuoteSnapshots.AsNoTracking().Where(x => x.CaseId == caseId)
                .OrderByDescending(x => x.CommercialRevision).FirstOrDefaultAsync(ct);
            var comparison = OperationsQuoteComparison.Compare(operation, snapshot);
            var history = await db.QuoteDiscrepancyReviews.AsNoTracking().Where(x => x.CaseId == caseId)
                .OrderByDescending(x => x.RequestedAtUtc).Take(50)
                .Select(x => new { x.Id, x.Fingerprint, x.Status, x.Reason, x.RequestedAtUtc, x.ResolvedAtUtc,
                    x.DecisionReason, x.CommercialRevision, x.DifferencesJson }).ToArrayAsync(ct);
            var current = history.FirstOrDefault(x => x.Fingerprint == comparison.Fingerprint);
            var canRequest = await CanRequestDiscrepancyAsync(operation, http, db, ct);
            return Results.Ok(new {
                comparison.HasSnapshot, comparison.Fingerprint, comparison.Rows, operation.Version,
                severity = OperationsQuoteComparison.Severity(comparison, current?.Status),
                currentReview = current, history, canRequest
            });
        });

        api.MapPost("/cases/{caseId:guid}/commercial/comparison/review", async (
            Guid caseId, RequestDiscrepancyReview input, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(input.Fingerprint) || input.Fingerprint.Length != 64 ||
                input.Comment?.Length > 300) return Results.BadRequest(new { code = "OPERATIONS_DISCREPANCY_INVALID" });
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var key = "operations-quote-discrepancy:" + caseId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
            var operation = await db.Cases.SingleOrDefaultAsync(x => x.Id == caseId, ct);
            if (operation is null) return Results.NotFound();
            if (!await CanRequestDiscrepancyAsync(operation, http, db, ct)) return Results.Forbid();
            var snapshot = await db.AcceptedQuoteSnapshots.AsNoTracking().Where(x => x.CaseId == caseId)
                .OrderByDescending(x => x.CommercialRevision).FirstOrDefaultAsync(ct);
            var comparison = OperationsQuoteComparison.Compare(operation, snapshot);
            if (snapshot is null || !comparison.Differences.Any() || input.Fingerprint != comparison.Fingerprint)
                return Results.Conflict(new { code = "OPERATIONS_DISCREPANCY_CHANGED" });
            var existing = await db.QuoteDiscrepancyReviews.AsNoTracking().FirstOrDefaultAsync(x =>
                x.CaseId == caseId && x.Fingerprint == comparison.Fingerprint, ct);
            if (existing is not null)
                return Results.Ok(new { reviewId = existing.Id, existing.Status, duplicate = true });
            if (operation.Version != input.ExpectedVersion)
                return Results.Conflict(new { code = "OPERATIONS_CASE_VERSION_CONFLICT" });
            var changes = comparison.Differences.ToArray();
            var reason = "Diferencias entre QUO aceptada y trámite: " + string.Join("; ",
                changes.Select(x => x.Label + ": " + x.Quoted + " → " + x.Current)) +
                (string.IsNullOrWhiteSpace(input.Comment) ? "" : ". Observación: " + input.Comment.Trim());
            // The existing Pricing contract allows 2000 characters. Never silently truncate evidence.
            if (reason.Length > 2000) return Results.BadRequest(new { code = "OPERATIONS_DISCREPANCY_REASON_TOO_LONG" });
            var review = new OperationsQuoteDiscrepancyReview {
                CaseId = caseId, QuoteId = snapshot.QuoteId, AcceptedOptionId = snapshot.AcceptedOptionId,
                CommercialRevision = snapshot.CommercialRevision, Fingerprint = comparison.Fingerprint,
                DifferencesJson = JsonSerializer.Serialize(changes), Reason = reason, RequestedBy = actor.Value
            };
            db.QuoteDiscrepancyReviews.Add(review);
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, caseId, null, actor.Value, "operations.pricing-review.requested", null,
                new { reviewId = review.Id, review.CommercialRevision, changes });
            // ServiceId is a legacy transport correlation field in Pricing's v1 contract.
            // For QUOTE_DISCREPANCY use the review ID, never create a fictional service.
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.pricing-review.requested.v1",
                JsonSerializer.Serialize(new {
                    caseId, operation.Idtra, operation.CompanyId, reviewRequestId = review.Id,
                    serviceId = review.Id, serviceCode = "QUOTE_DISCREPANCY", review.Reason,
                    requestedBy = actor.Value, requestedAtUtc = review.RequestedAtUtc,
                    quoteId = review.QuoteId, acceptedOptionId = review.AcceptedOptionId
                })));
            db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.discrepancy.detected.v1",
                JsonSerializer.Serialize(new { caseId, reviewRequestId = review.Id, severity = "YELLOW", version = operation.Version })));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "OPERATIONS_CASE_VERSION_CONFLICT" }); }
            await tx.CommitAsync(ct);
            return Results.Ok(new { reviewId = review.Id, review.Status, duplicate = false });
        });

        api.MapGet("/cases/{caseId:guid}/services-and-reviews", async (
            Guid caseId, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(caseId, http.User, ct))
                return Results.Forbid();
            var services = await db.CaseServiceLines.AsNoTracking()
                .Where(x => x.CaseId == caseId).OrderBy(x => x.CreatedAtUtc)
                .Select(x => new { x.Id, x.ServiceCode, x.ServiceName, x.Source,
                    x.RequiresPricingReview, x.ReviewStatus })
                .ToArrayAsync(ct);
            var alerts = await db.CommercialAlerts.AsNoTracking()
                .Where(x => x.CaseId == caseId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Select(x => new { x.Id, x.ServiceLineId, x.Code, x.Severity,
                    x.Status, x.CreatedAtUtc, x.ResolvedAtUtc })
                .ToArrayAsync(ct);
            var reviews = await db.PricingReviewRequests.AsNoTracking()
                .Where(x => x.CaseId == caseId)
                .OrderByDescending(x => x.RequestedAtUtc)
                .Select(x => new { x.Id, x.ServiceLineId, x.QuoteId,
                    x.AcceptedOptionId, x.Reason, x.Status, x.RequestedAtUtc,
                    x.ResolvedAtUtc })
                .ToArrayAsync(ct);
            return Results.Ok(new { services, alerts, reviews });
        });

        api.MapPost("/cases/{caseId:guid}/services", async (
            Guid caseId, AddServiceRequest input,
            HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null ||
                !(OperationsCaseAccess.HasScope(http.User, "operations.case.update") ||
                  OperationsCaseAccess.HasScope(http.User, "operations.origin.update") ||
                  OperationsCaseAccess.HasScope(http.User, "operations.aperture.update")) ||
                !await access.CanReadAsync(caseId, http.User, ct))
                return Results.Forbid();
            if (input.IdempotencyKey == Guid.Empty ||
                string.IsNullOrWhiteSpace(input.ServiceCode) ||
                input.ServiceCode.Length > 60 ||
                !input.ServiceCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ||
                string.IsNullOrWhiteSpace(input.ServiceName) ||
                input.ServiceName.Length > 200 ||
                string.IsNullOrWhiteSpace(input.Reason) ||
                input.Reason.Trim().Length is < 10 or > 2000)
                return Results.BadRequest(new { code = "OPERATIONS_ADDED_SERVICE_INVALID" });

            await using var tx = await db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, ct);
            var operation = await db.Cases.SingleOrDefaultAsync(c =>
                c.Id == caseId, ct);
            if (operation is null) return Results.NotFound();
            if (operation.Status is "CANCELLED" or "COMPLETED")
                return Results.Conflict(new { code = "OPERATIONS_CASE_NOT_EDITABLE" });
            // Serializes all additions for the same case. Event retries are
            // idempotent even when different browser tabs submit together.
            var lockKey = "operations-added-service:" + caseId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
            var existing = await db.CaseServiceLines.AsNoTracking()
                .Where(x => x.CaseId == caseId &&
                    x.IdempotencyKey == input.IdempotencyKey &&
                    x.Source == "OPERATIONS_ADDED")
                .Select(x => new { x.Id, x.ServiceCode, x.ReviewStatus })
                .SingleOrDefaultAsync(ct);
            if (existing is not null)
            {
                var review = await db.PricingReviewRequests.AsNoTracking()
                    .Where(x => x.ServiceLineId == existing.Id)
                    .Select(x => new { x.Id, x.Status }).SingleAsync(ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new {
                    serviceId = existing.Id, existing.ServiceCode,
                    reviewId = review.Id, review.Status, severity = "YELLOW",
                    duplicate = true
                });
            }

            var service = new OperationsCaseServiceLine
            {
                CaseId = caseId, IdempotencyKey = input.IdempotencyKey,
                ServiceCode = input.ServiceCode.Trim().ToUpperInvariant(),
                ServiceName = input.ServiceName.Trim(), CreatedBy = actor.Value,
                Source = "OPERATIONS_ADDED", RequiresPricingReview = true,
                ReviewStatus = "PENDING_PRICING"
            };
            var alert = new OperationsCommercialAlert
            {
                CaseId = caseId, ServiceLineId = service.Id,
                Code = "UNQUOTED_SERVICE", Severity = "YELLOW",
                Status = "PENDING_PRICING", CreatedBy = actor.Value
            };
            var reviewRequest = new OperationsPricingReviewRequest
            {
                CaseId = caseId, ServiceLineId = service.Id,
                QuoteId = operation.SourceQuoteId,
                AcceptedOptionId = operation.SourceQuoteOptionId,
                Reason = input.Reason.Trim(), RequestedBy = actor.Value
            };
            db.CaseServiceLines.Add(service);
            db.CommercialAlerts.Add(alert);
            db.PricingReviewRequests.Add(reviewRequest);
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, caseId, null, actor.Value,
                "operations.pricing-review.requested", null,
                new { serviceId = service.Id, alertId = alert.Id,
                    reviewId = reviewRequest.Id, service.ServiceCode });
            // Separate durable signals: commercial request goes to Pricing's
            // own stream; user-facing realtime includes IDs/versions only.
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.pricing-review.requested.v1",
                JsonSerializer.Serialize(new {
                    caseId, operation.Idtra, operation.CompanyId,
                    reviewRequestId = reviewRequest.Id,
                    serviceId = service.Id, service.ServiceCode,
                    reviewRequest.Reason, requestedBy = actor.Value,
                    requestedAtUtc = reviewRequest.RequestedAtUtc,
                    quoteId = operation.SourceQuoteId,
                    acceptedOptionId = operation.SourceQuoteOptionId
                })));
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.discrepancy.detected.v1",
                JsonSerializer.Serialize(new {
                    caseId, alertId = alert.Id, reviewRequestId = reviewRequest.Id,
                    severity = "YELLOW", version = operation.Version
                })));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/operations/cases/{caseId}/services-and-reviews",
                new { serviceId = service.Id, reviewId = reviewRequest.Id,
                    alertId = alert.Id, severity = alert.Severity,
                    reviewStatus = reviewRequest.Status, duplicate = false });
        });

        return app;
    }

    private static async Task<bool> CanRequestDiscrepancyAsync(OperationsCase operation, HttpContext http,
        OperationsDbContext db, CancellationToken ct)
    {
        if (operation.Status is "CANCELLED" or "COMPLETED" || operation.SourceQuoteId is null) return false;
        var actor = OperationsCaseAccess.UserId(http.User);
        if (actor is null || !(OperationsCaseAccess.HasScope(http.User, "operations.case.update") ||
            OperationsCaseAccess.HasScope(http.User, "operations.origin.update") ||
            OperationsCaseAccess.HasScope(http.User, "operations.aperture.update"))) return false;
        if (OperationsCaseAccess.IsSystemAdministrator(http.User)) return true;
        return await db.CaseStages.AsNoTracking().AnyAsync(s => s.CaseId == operation.Id &&
            s.Status != "COMPLETED" && s.Status != "SKIPPED" &&
            (db.StageAssignments.Any(a => a.CaseStageId == s.Id && a.UserId == actor.Value && a.ReleasedAtUtc == null) ||
             db.ApertureValidations.Any(v => v.CaseStageId == s.Id && v.UserId == actor.Value && v.Status == "PENDING") ||
             db.TeamMembers.Any(m => m.TeamId == s.TeamId && m.UserId == actor.Value && m.IsSupervisor && m.IsAvailable)), ct);
    }
}
