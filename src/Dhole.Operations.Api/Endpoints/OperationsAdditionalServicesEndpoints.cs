using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>Extra unquoted services are never written back to the accepted commercial quote.</summary>
public static class OperationsAdditionalServicesEndpoints
{
    public sealed record AddAdditionalServiceRequest(
        Guid RequestId, string ServiceCode, string ServiceName, string Reason);

    public static IEndpointRouteBuilder MapOperationsAdditionalServicesEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations/cases/{caseId:guid}/additional-services")
            .RequireAuthorization();
        api.MapGet("", async (Guid caseId, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            var rows = await db.AdditionalServices.AsNoTracking()
                .Where(x => x.CaseId == caseId).OrderByDescending(x => x.AddedAtUtc)
                .Select(x => new
                {
                    x.Id, x.ServiceCode, x.ServiceName, x.Reason, x.Source,
                    x.ReviewStatus, x.SourceQuoteId, x.AddedBy,
                    x.AddedAtUtc, x.ReviewedAtUtc, x.PricingResolutionNotes
                }).ToArrayAsync(ct);
            return Results.Ok(rows);
        });
        api.MapPost("", async (Guid caseId, AddAdditionalServiceRequest input,
            HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null ||
                !(OperationsCaseAccess.HasScope(http.User, "operations.case.create") ||
                  OperationsCaseAccess.HasScope(http.User, "operations.origin.update")) ||
                !await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            if (input.RequestId == Guid.Empty ||
                string.IsNullOrWhiteSpace(input.ServiceCode) || input.ServiceCode.Length > 80 ||
                string.IsNullOrWhiteSpace(input.ServiceName) || input.ServiceName.Length > 300 ||
                string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 2000)
                return Results.BadRequest(new { code = "OPERATIONS_ADDITIONAL_SERVICE_INVALID" });

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var lockKey = "operations-added-service:" + caseId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
            var operation = await db.Cases.SingleOrDefaultAsync(x => x.Id == caseId, ct);
            if (operation is null) return Results.NotFound();
            if (operation.Status is "CANCELLED" or "COMPLETED")
                return Results.Conflict(new { code = "OPERATIONS_CASE_CLOSED" });

            var existing = await db.AdditionalServices.SingleOrDefaultAsync(x => x.Id == input.RequestId, ct);
            if (existing is not null)
            {
                if (existing.CaseId != caseId) return Results.Conflict(new { code = "OPERATIONS_SERVICE_ID_REUSED" });
                return Results.Ok(new { existing.Id, existing.ReviewStatus, duplicate = true });
            }

            var service = new OperationsAdditionalService
            {
                Id = input.RequestId,
                CaseId = caseId,
                SourceQuoteId = operation.SourceQuoteId,
                ServiceCode = input.ServiceCode.Trim().ToUpperInvariant(),
                ServiceName = input.ServiceName.Trim(),
                Reason = input.Reason.Trim(),
                AddedBy = actor.Value
            };
            db.AdditionalServices.Add(service);
            var stage = await db.CaseStages.AsNoTracking()
                .Where(x => x.CaseId == caseId && x.DepartmentCode == "APERTURAS")
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            OperationsAuditWriter.Case(db, caseId, stage, actor.Value,
                "operations.additional_service.requested", null,
                new { service.Id, service.ServiceCode, service.ServiceName, service.Source,
                    service.ReviewStatus, service.SourceQuoteId });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.case.additional_service.requested.v1",
                JsonSerializer.Serialize(new { caseId, operation.CompanyId, serviceId = service.Id,
                    status = service.ReviewStatus, source = service.Source })));
            if (service.SourceQuoteId.HasValue)
            {
                db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                    "operations.pricing.review.requested.v1",
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        reviewRequestId = service.Id,
                        caseId, operation.Idtra, operation.CompanyId,
                        quoteId = service.SourceQuoteId.Value,
                        serviceCode = service.ServiceCode, serviceName = service.ServiceName,
                        reason = service.Reason, addedBy = actor.Value,
                        addedAtUtc = service.AddedAtUtc
                    })));
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created($"/api/operations/cases/{caseId}/additional-services",
                new { service.Id, service.ReviewStatus, duplicate = false });
        });
        return app;
    }
}
