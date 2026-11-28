using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Integrations;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dhole.Operations.Api.Endpoints;

public static class OperationsItineraryEndpoints
{
    public sealed record CreateDraft(Guid RequestId, long ExpectedCaseVersion, OperationsItineraryPolicy.LegInput[] Legs);
    public sealed record UpdateEstimates(long ExpectedCaseVersion, long ExpectedLegVersion, string? Etd, string? Eta);

    private static bool Enabled(IConfiguration config) => config.GetValue<bool>("OperationsItinerary:Enabled");
    private static bool Has(HttpContext http, string scope) => OperationsCaseAccess.HasScope(http.User, scope);
    private static bool CountryGrant(HttpContext http, string country) =>
        Has(http, "operations.destination.all") ||
        (country is "PA" or "CR" or "US" && Has(http, "operations.destination." + country.ToLowerInvariant()));

    public static IEndpointRouteBuilder MapOperationsItineraryEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();
        api.MapGet("/cases/{id:guid}/itinerary", async (Guid id, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var operation = await db.Cases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
            if (operation is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, no-store";
            if (!Enabled(config)) return Results.Ok(new { caseId = id, caseVersion = operation.Version, enabled = false,
                transit = Array.Empty<object>(), destinations = Array.Empty<object>() });

            var company = OperationsCaseAccess.IsSystemAdministrator(http.User) ? null : await access.ResolveCompanyIdAsync(http.User, ct);
            var visible = db.Cases.AsNoTracking().Where(c => c.Id == id);
            var stages = await db.CaseStages.AsNoTracking().Where(s => s.CaseId == id && s.Status != "SKIPPED").ToArrayAsync(ct);
            var authorized = new HashSet<Guid>();
            foreach (var stage in stages)
                if (await access.FilterVisibleCases(visible, http.User, company, department: stage.DepartmentCode).AnyAsync(ct))
                    authorized.Add(stage.Id);

            var legs = await db.ItineraryLegs.AsNoTracking().Where(x => x.CaseId == id).OrderBy(x => x.Sequence).ToArrayAsync(ct);
            var transit = (Has(http, "operations.transit.read") || Has(http, "operations.transit.manage"))
                ? legs.Where(x => authorized.Contains(x.TransitStageId) &&
                    stages.Any(s => s.Id == x.TransitStageId && s.DepartmentCode == "TRANSITO")).Select(x => new {
                    x.Id, x.Sequence, x.TransportCode, x.FromLocationId, x.ToLocationId, x.FromLabel, x.ToLabel,
                    x.FromCountryCode, x.ToCountryCode, x.Status, x.Version,
                    x.PlannedDepartureAtUtc, x.PlannedArrivalAtUtc, x.EstimatedDepartureAtUtc, x.EstimatedArrivalAtUtc,
                    x.ActualDepartureAtUtc, x.ActualArrivalAtUtc,
                    estimatedTransit = OperationsOriginTransitDuration.Calculate(x.EstimatedDepartureAtUtc?.ToString("O"), x.EstimatedArrivalAtUtc?.ToString("O"))
                }).Cast<object>().ToArray() : [];
            // No full itinerary, other-country location, provider, price or task
            // data is included in the country-specific projection.
            var destinations = legs.Where(x => x.DestinationStageId.HasValue && x.DestinationKind is "FINAL" or "ENTRY_TRANSFER" &&
                OperationsItineraryPolicy.CanReadDestination(x.ToCountryCode, Has(http, "operations.destination.read"),
                    CountryGrant(http, x.ToCountryCode), false, authorized.Contains(x.DestinationStageId.Value)) &&
                stages.Any(s => s.Id == x.DestinationStageId && s.DepartmentCode == OperationsItineraryPolicy.DestinationDepartment(x.ToCountryCode)))
                .Select(x => new { legId = x.Id, x.Sequence, countryCode = x.ToCountryCode, locationId = x.ToLocationId,
                    locationLabel = x.ToLabel, kind = x.DestinationKind, stageId = x.DestinationStageId,
                    stageStatus = stages.Single(s => s.Id == x.DestinationStageId).Status,
                    configurationState = "PENDING_APPROVAL" }).ToArray();
            return Results.Ok(new { caseId = id, caseVersion = operation.Version, enabled = true, transit, destinations });
        });

        api.MapPost("/cases/{id:guid}/itinerary/draft", async (Guid id, CreateDraft input, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, IConfiguration config, OperationsItineraryCatalog catalog, CancellationToken ct) =>
        {
            if (!Enabled(config)) return Results.Conflict(new { code = "OPERATIONS_ITINERARY_DISABLED" });
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !Has(http, "operations.itinerary.configure") || !Has(http, "operations.case.update") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            if (input.RequestId == Guid.Empty || input.ExpectedCaseVersion < 1 ||
                input.Legs is null || input.Legs.Length is < 1 or > 20 || input.Legs.Any(x => x is null))
                return Results.BadRequest(new { code = "OPERATIONS_ITINERARY_INVALID" });

            var ids = input.Legs.SelectMany(x => new[] { x.FromLocationId, x.ToLocationId }).Distinct().ToArray();
            if (ids.Contains(Guid.Empty)) return Results.BadRequest(new { code = "OPERATIONS_LOCATION_INVALID" });
            var locations = await catalog.ResolveAsync(ids, http.Request.Headers.Authorization.ToString(), ct);
            if (locations is null) return Results.Conflict(new { code = "OPERATIONS_LOCATION_UNVERIFIED" });

            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var key = "itinerary-draft:" + id.ToString("N");
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
                if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
                var operation = await db.Cases.SingleOrDefaultAsync(c => c.Id == id, ct);
                if (operation is null || operation.Status is "COMPLETED" or "CANCELLED") return Results.Forbid();
                var validation = OperationsItineraryPolicy.Validate(input.Legs, locations, operation.OriginCountryCode, operation.DestinationCountryCode);
                if (validation is not null) return Results.BadRequest(new { code = validation });
                // Preserve explicit commercial route anchors. Missing legacy IDs
                // are not inferred from labels and require an explicit draft.
                var opening = OperationsCargoMeasurements.Parse(operation.OpeningDataJson);
                bool Anchor(string name, Guid actual) => !Guid.TryParse(opening[name]?.ToString(), out var expected) ||
                    expected == Guid.Empty || expected == actual;
                if (!Anchor("polCatalogId", input.Legs[0].FromLocationId) ||
                    !Anchor("podCatalogId", input.Legs[^1].ToLocationId) ||
                    (Guid.TryParse(opening["poeCatalogId"]?.ToString(), out var poe) && poe != Guid.Empty &&
                        !input.Legs.Any(x => x.ToLocationId == poe)))
                    return Results.Conflict(new { code = "OPERATIONS_ITINERARY_ROUTE_CONFLICT" });

                var stages = await db.CaseStages.AsNoTracking().Where(s => s.CaseId == id && s.Status != "COMPLETED" && s.Status != "SKIPPED").ToArrayAsync(ct);
                foreach (var leg in input.Legs)
                {
                    if (!Has(http, "operations.transit.read") || !stages.Any(s => s.Id == leg.TransitStageId && s.DepartmentCode == "TRANSITO") ||
                        !await access.CanEditAssignedStageAsync(leg.TransitStageId, http.User, ct)) return Results.Forbid();
                    if (leg.DestinationStageId.HasValue)
                    {
                        var country = locations[leg.ToLocationId].CountryCode;
                        if (!Has(http, "operations.destination.read") || !CountryGrant(http, country) ||
                            !stages.Any(s => s.Id == leg.DestinationStageId && s.DepartmentCode == OperationsItineraryPolicy.DestinationDepartment(country)) ||
                            !await access.CanEditAssignedStageAsync(leg.DestinationStageId.Value, http.User, ct)) return Results.Forbid();
                    }
                    if (leg.DestinationServiceLineId.HasValue && !await db.CaseServiceLines.AsNoTracking().AnyAsync(s =>
                        s.Id == leg.DestinationServiceLineId && s.CaseId == id && !s.RequiresPricingReview, ct))
                        return Results.Conflict(new { code = "OPERATIONS_ITINERARY_SERVICE_UNVERIFIED" });
                }
                var serviceIds = input.Legs.Where(x => x.DestinationServiceLineId.HasValue).Select(x => x.DestinationServiceLineId).ToArray();
                if (serviceIds.Distinct().Count() != serviceIds.Length)
                    return Results.Conflict(new { code = "OPERATIONS_ITINERARY_SERVICE_ALLOCATION_REVIEW" });

                var existing = await db.ItineraryLegs.AsNoTracking().Where(x => x.CaseId == id).OrderBy(x => x.Sequence).ToArrayAsync(ct);
                if (existing.Length > 0)
                {
                    // Create-only: exact replay can return existing IDs; never
                    // replace legs referenced by later observations/documents.
                    var same = existing.Length == input.Legs.Length && existing.Zip(input.Legs).All(pair =>
                        pair.First.PlanRequestId == input.RequestId && pair.First.CreatedBy == actor.Value &&
                        pair.First.FromLocationId == pair.Second.FromLocationId && pair.First.ToLocationId == pair.Second.ToLocationId &&
                        pair.First.TransportCode == pair.Second.TransportCode && pair.First.TransitStageId == pair.Second.TransitStageId &&
                        pair.First.DestinationKind == pair.Second.DestinationKind && pair.First.DestinationStageId == pair.Second.DestinationStageId &&
                        pair.First.DestinationServiceLineId == pair.Second.DestinationServiceLineId);
                    return same ? Results.Ok(new { caseId = id, caseVersion = operation.Version, duplicate = true, legIds = existing.Select(x => x.Id) })
                        : Results.Conflict(new { code = "OPERATIONS_ITINERARY_ALREADY_EXISTS" });
                }
                if (operation.Version != input.ExpectedCaseVersion) return Results.Conflict(new { code = "OPERATIONS_CASE_VERSION_CONFLICT" });
                var now = DateTimeOffset.UtcNow;
                var legs = input.Legs.Select((x, i) => new OperationsItineraryLeg {
                    CaseId = id, PlanRequestId = input.RequestId, Sequence = i + 1, TransportCode = x.TransportCode,
                    FromLocationId = x.FromLocationId, ToLocationId = x.ToLocationId,
                    FromLabel = locations[x.FromLocationId].Label, ToLabel = locations[x.ToLocationId].Label,
                    FromCountryCode = locations[x.FromLocationId].CountryCode, ToCountryCode = locations[x.ToLocationId].CountryCode,
                    TransitStageId = x.TransitStageId, DestinationKind = x.DestinationKind,
                    DestinationStageId = x.DestinationStageId, DestinationServiceLineId = x.DestinationServiceLineId,
                    CreatedBy = actor.Value, CreatedAtUtc = now
                }).ToArray();
                db.ItineraryLegs.AddRange(legs);
                operation.Version++; operation.UpdatedAtUtc = now;
                OperationsAuditWriter.Case(db, id, null, actor.Value, "operations.itinerary.draft.created", null,
                    new { requestId = input.RequestId, legIds = legs.Select(x => x.Id), operation.Version });
                db.OutboxMessages.Add(OperationsOutboxFactory.Create("operations.itinerary.changed.v1",
                    JsonSerializer.Serialize(new { caseId = id, version = operation.Version })));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Results.Created($"/api/operations/cases/{id}/itinerary", new { caseId = id, caseVersion = operation.Version,
                    duplicate = false, legIds = legs.Select(x => x.Id) });
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "OPERATIONS_ITINERARY_VERSION_CONFLICT" }); }
            catch (DbUpdateException e) when (e.InnerException is PostgresException p && p.SqlState is "23505" or "40001" or "40P01")
            { return Results.Conflict(new { code = "OPERATIONS_ITINERARY_RETRY_REQUIRED" }); }
            catch (PostgresException e) when (e.SqlState is "40001" or "40P01")
            { return Results.Conflict(new { code = "OPERATIONS_ITINERARY_RETRY_REQUIRED" }); }
        });
        return app;
    }
}
