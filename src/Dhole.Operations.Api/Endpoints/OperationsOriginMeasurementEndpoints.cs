using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>Origin alone records and normalizes actual weight/volume without changing the accepted quote.</summary>
public static class OperationsOriginMeasurementEndpoints
{
    public sealed record SaveOriginMeasurementRequest(
        long ExpectedVersion, decimal? WeightValue, string? WeightUnit,
        decimal? VolumeValue, string? VolumeUnit);

    public static IEndpointRouteBuilder MapOperationsOriginMeasurementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operations/cases/{caseId:guid}/origin/measurements")
            .RequireAuthorization();

        group.MapGet("", async (Guid caseId, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            var measurement = await db.OriginMeasurements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.CaseId == caseId, ct);
            return measurement is null ? Results.Ok(new { caseId, captured = false }) :
                Results.Ok(new
                {
                    caseId, captured = true, measurement.OriginalWeight,
                    measurement.WeightUnit, measurement.WeightKg, measurement.OriginalVolume,
                    measurement.VolumeUnit, measurement.VolumeCbm, measurement.ConfirmedBy,
                    measurement.ConfirmedAtUtc, measurement.Version
                });
        });

        group.MapPut("", async (Guid caseId, SaveOriginMeasurementRequest input,
            HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null ||
                !OperationsCaseAccess.HasScope(http.User, "operations.origin.update") ||
                !await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();

            if (input.ExpectedVersion < 0 ||
                (input.WeightValue is null && input.VolumeValue is null) ||
                (input.WeightValue is < 0 or > 100000000) ||
                (input.VolumeValue is < 0 or > 100000000))
                return Results.BadRequest(new { code = "OPERATIONS_MEASUREMENT_INVALID" });

            var weightUnit = input.WeightUnit?.Trim().ToUpperInvariant();
            var volumeUnit = input.VolumeUnit?.Trim().ToUpperInvariant();
            if ((input.WeightValue.HasValue && weightUnit is not ("KG" or "LB")) ||
                (input.VolumeValue.HasValue && volumeUnit is not ("CBM" or "CFT")))
                return Results.BadRequest(new { code = "OPERATIONS_MEASUREMENT_UNIT_INVALID" });

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var lockKey = "origin-measurements:" + caseId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);
            var operation = await db.Cases.SingleOrDefaultAsync(c => c.Id == caseId, ct);
            if (operation is null) return Results.NotFound();
            var origin = await db.CaseStages.SingleOrDefaultAsync(s =>
                s.CaseId == caseId && s.DepartmentCode == "COORDINACION_ORIGEN", ct);
            if (origin is null || operation.Status is "DRAFT" or "PENDING_VALIDATION" or "APERTURA_REQUIERE_CAMBIOS")
                return Results.Conflict(new { code = "OPERATIONS_ORIGIN_NOT_RELEASED" });
            var existing = await db.OriginMeasurements.SingleOrDefaultAsync(x => x.CaseId == caseId, ct);
            if (existing is null && input.ExpectedVersion != 0 ||
                existing is not null && input.ExpectedVersion != existing.Version)
                return Results.Conflict(new { code = "OPERATIONS_MEASUREMENT_VERSION_CONFLICT" });

            var before = existing is null ? null : new
            {
                existing.OriginalWeight, existing.WeightUnit, existing.WeightKg,
                existing.OriginalVolume, existing.VolumeUnit, existing.VolumeCbm
            };
            if (existing is null)
            {
                existing = new OperationsOriginMeasurement { CaseId = caseId };
                db.OriginMeasurements.Add(existing);
            }
            existing.OriginalWeight = input.WeightValue;
            existing.WeightUnit = input.WeightValue.HasValue ? weightUnit : null;
            existing.WeightKg = input.WeightValue.HasValue
                ? decimal.Round(input.WeightValue.Value * (weightUnit == "LB" ? 0.45359237m : 1m), 6)
                : null;
            existing.OriginalVolume = input.VolumeValue;
            existing.VolumeUnit = input.VolumeValue.HasValue ? volumeUnit : null;
            existing.VolumeCbm = input.VolumeValue.HasValue
                ? decimal.Round(input.VolumeValue.Value * (volumeUnit == "CFT" ? 0.028316846592m : 1m), 6)
                : null;
            existing.ConfirmedBy = actor.Value;
            existing.ConfirmedAtUtc = DateTimeOffset.UtcNow;
            existing.Version = input.ExpectedVersion + 1;

            OperationsAuditWriter.Case(db, caseId, origin.Id, actor.Value,
                "operations.origin.measurements.confirmed", before, new
                {
                    existing.OriginalWeight, existing.WeightUnit, existing.WeightKg,
                    existing.OriginalVolume, existing.VolumeUnit, existing.VolumeCbm
                });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.origin.measurements.confirmed.v1",
                JsonSerializer.Serialize(new
                {
                    caseId, operation.CompanyId, existing.Version,
                    existing.WeightKg, existing.VolumeCbm
                })));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new
            {
                caseId, existing.Version, existing.OriginalWeight,
                existing.WeightUnit, existing.WeightKg, existing.OriginalVolume,
                existing.VolumeUnit, existing.VolumeCbm, existing.ConfirmedAtUtc
            });
        });
        return app;
    }
}
