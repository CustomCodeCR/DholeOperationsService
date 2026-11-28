using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;
using static Dhole.Operations.Domain.Services.OperationsTransitionRules;

namespace Dhole.Operations.Api.Integrations;

public sealed class OperationsTransitionChecklist(IHttpClientFactory factory, IConfiguration configuration)
{
    private sealed record CatalogRow(Guid Id, string? MetadataJson);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public bool EnforcementEnabled => configuration.GetValue<bool>("OperationsTransitions:EnforceOriginDeparture");

    private async Task<Rule[]?> ReadRules(string authorization, CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration["Config:ApiBaseUrl"], UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !AuthenticationHeaderValue.TryParse(authorization, out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(bearer.Parameter)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"),
            "api/config/operations/transition-rules"));
        request.Headers.Authorization = bearer;
        try
        {
            using var response = await factory.CreateClient("config-operations-opening").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var rows = await response.Content.ReadFromJsonAsync<CatalogRow[]>(cancellationToken: ct);
            if (rows is null || rows.Length > 200 || rows.Select(r => r.Id).Distinct().Count() != rows.Length) return null;
            var result = new List<Rule>();
            foreach (var row in rows)
            {
                if (row.Id == Guid.Empty || string.IsNullOrWhiteSpace(row.MetadataJson) || row.MetadataJson.Length > 32000)
                    return null;
                var rule = JsonSerializer.Deserialize<Rule>(row.MetadataJson, JsonOptions);
                if (rule is null || (rule.Id != Guid.Empty && rule.Id != row.Id)) return null;
                result.Add(rule with { Id = row.Id });
            }
            return result.ToArray();
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public async Task<Evaluation> EvaluateAsync(OperationsDbContext db, OperationsCase operation,
        OperationsCaseStage stage, string authorization, CancellationToken ct,
        Guid? verifiedProposedEvidence = null)
    {
        var rules = await ReadRules(authorization, ct);
        if (rules is null) return new("CONFIG_UNAVAILABLE", null, null, null, false, []);
        // No stable leg mapping yet: never approve an ambiguous multimodal transfer.
        if (operation.ModalityCode == "MULTIMODAL")
            return new("LEG_UNRESOLVED", null, null, null, false, []);
        var opening = OperationsCargoMeasurements.Parse(operation.OpeningDataJson);
        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fromStage"] = stage.DepartmentCode, ["toStage"] = "TRANSITO",
            ["companyId"] = operation.CompanyId.ToString("D"), ["shipment"] = operation.ModalityCode
        };
        void Set(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) dimensions[key] = value.Trim(); }
        Set("teamId", stage.TeamId?.ToString("D"));
        Set("transport", opening["transportType"]?.ToString());
        Set("originCountry", operation.OriginCountryCode);
        Set("destinationCountry", operation.DestinationCountryCode);
        Set("incoterm", opening["incoterm"]?.ToString());
        foreach (var pair in new[] { ("polId", "polCatalogId"), ("poeId", "poeCatalogId"), ("podId", "podCatalogId") })
            if (Guid.TryParse(opening[pair.Item2]?.ToString(), out var portId) && portId != Guid.Empty) Set(pair.Item1, portId.ToString("D"));
        // Do not invent legType or infer service IDs from translated labels.
        var services = (await db.CaseServiceLines.AsNoTracking().Where(s => s.CaseId == operation.Id)
            .Select(s => s.ServiceCode).ToArrayAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
        Fact DateFact(string? value) => OperationsOriginTransitDuration.Calculate(value, value).State == "CALCULATED"
            ? new("CONFIRMED", "OPERATIONS_PLANNING", "ESTIMATE_RECORDED")
            : new("PENDING", "OPERATIONS_PLANNING", "ESTIMATE_MISSING_OR_INVALID");
        var etd = opening["etm"]?.ToString();
        var eta = opening["eta"]?.ToString();
        facts["ETD"] = DateFact(etd); facts["ETA"] = DateFact(eta);
        if (OperationsOriginTransitDuration.Calculate(etd, eta).State == "INVALID")
            facts["ETD"] = facts["ETA"] = new("PENDING", "OPERATIONS_PLANNING", "INCONSISTENT_ESTIMATES");

        var progress = await db.OriginProgress.AsNoTracking().SingleOrDefaultAsync(p => p.CaseStageId == stage.Id, ct);
        var documents = await db.Documents.AsNoTracking().Where(d => d.CaseId == operation.Id &&
            d.OwnerStageId == stage.Id && d.AccessClass == OperationsDocumentPolicy.Operational &&
            d.Versions.Any()).Select(d => new { d.DocumentTypeCode, d.Status,
                Latest = d.Versions.OrderByDescending(v => v.VersionNumber)
                    .Select(v => new { v.StorageFileId, v.ReviewStatus }).First() }).ToArrayAsync(ct);
        var evidence = verifiedProposedEvidence ?? progress?.EvidenceStorageFileId;
        var departureVerified = (verifiedProposedEvidence.HasValue || progress?.ActualDepartureAtUtc is not null) &&
            evidence.HasValue && documents.Any(d => d.Status == "APPROVED" &&
                d.Latest.ReviewStatus == "APPROVED" && d.Latest.StorageFileId == evidence.Value);
        facts["ACTUAL_DEPARTURE"] = departureVerified
            ? new("CONFIRMED", "OPERATIONS_DEPARTURE", "APPROVED_EVIDENCE_REGISTERED")
            : new("PENDING", "OPERATIONS_DEPARTURE", "ACTUAL_DEPARTURE_EVIDENCE_REQUIRED");

        foreach (var type in new[] { "HBL", "MBL", "BOOKING", "SHIPPING_ORDER" })
            facts[type] = documents.Any(d => d.DocumentTypeCode == type && d.Status == "APPROVED" && d.Latest.ReviewStatus == "APPROVED")
                ? new("CONFIRMED", "OPERATIONS_DOCUMENTS", "APPROVED_DOCUMENT")
                : new("PENDING", "OPERATIONS_DOCUMENTS", "APPROVED_DOCUMENT_REQUIRED");

        var outstanding = await db.PricingReviewRequests.AsNoTracking().AnyAsync(r =>
            r.CaseId == operation.Id && r.Status != "RESOLVED_NO_PRICE_CHANGE", ct) ||
            await db.QuoteDiscrepancyReviews.AsNoTracking().AnyAsync(r =>
                r.CaseId == operation.Id && r.Status != "RESOLVED_NO_PRICE_CHANGE", ct) ||
            await db.CommercialAlerts.AsNoTracking().AnyAsync(r => r.CaseId == operation.Id && r.Status != "RESOLVED", ct);
        facts["PRICING_RESOLVED"] = outstanding
            ? new("PENDING", "PRICING_REVIEW", "REGISTERED_REVIEW_OUTSTANDING")
            : new("CONFIRMED", "PRICING_REVIEW", "NO_OUTSTANDING_REGISTERED_REVIEW");
        // Other fields are explicitly UNVERIFIED until their source contracts
        // from F2/F3 exist. No guessed completion from an opening label.
        return Evaluate(rules, new Context(dimensions, services), facts, DateTimeOffset.UtcNow);
    }
}
