using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Api.Integrations;
using System.Net;
using System.Net.Http.Headers;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Application.Cases;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

public static class OperationsEndpoints
{
    public sealed record NewCaseRequest(
        Guid CompanyId, Guid AccountId, string ModalityCode,
        Guid? ExecutiveUserId = null, short Priority = 1, decimal ComplexityPoints = 1m,
        string? OriginCountryCode = null, string? DestinationCountryCode = null,
        System.Text.Json.JsonElement? OpeningData = null, Guid? QuoteRateId = null);

    public sealed record IdtraSequenceRequest(long NextNumber, long ExpectedNextNumber, string Reason);
    public sealed record NewTeamRequest(Guid CompanyId, string DepartmentCode, string Name);
    public sealed record NewMemberRequest(Guid UserId, decimal CapacityPoints, bool IsSupervisor, bool IsAvailable = true, string? RoleCode = null);
    public sealed record NewMembersBatchRequest(Guid[] UserIds, decimal CapacityPoints,
        bool IsSupervisor, bool IsAvailable = true, string? RoleCode = null);
    public sealed record NewAssignmentRuleRequest(
        Guid TeamId, string DepartmentCode, string? ModalityCode,
        string? OriginCountryCode, string? DestinationCountryCode, int Priority = 0);
    public sealed record NewDocumentRequest(
        string DocumentTypeCode, Guid StorageFileId, string FileName, string MimeType, string? Sha256);

    private static string ValidateOpeningData(System.Text.Json.JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
            return "{}";
        if (element.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ArgumentException("Opening data must be a JSON object.");
        var json = element.Value.GetRawText();
        if (json.Length > 65536)
            throw new ArgumentException("Opening payload exceeds maximum size.");
        // Operational requirements are captured for the opening, not re-priced here.
        // Detailed validation occurs before approval; incomplete drafts are permitted.
        var data = OperationsCargoMeasurements.Parse(json);
        OperationsCargoMeasurements.RejectOriginOnCreate(data);
        OperationsCargoMeasurements.NormalizeOpening(data);
        return data.ToJsonString();
    }

    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();

        api.MapGet("/idtra-sequence", async (HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User)) return Results.Forbid();
            return Results.Ok(new { nextNumber = await Dhole.Operations.Persistence.Cases.OperationsCaseStore.ReadNextIdtraAsync(db, ct) });
        });
        api.MapPut("/idtra-sequence", async (IdtraSequenceRequest request, HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.IsSystemAdministrator(http.User)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest(new { code = "IDTRA_REASON_REQUIRED" });
            try
            {
                var next = await Dhole.Operations.Persistence.Cases.OperationsCaseStore.ConfigureNextIdtraAsync(
                    db, request.NextNumber, request.ExpectedNextNumber, actor.Value, request.Reason, ct);
                return next.HasValue ? Results.Ok(new { nextNumber = next.Value })
                    : Results.Conflict(new { code = "IDTRA_SEQUENCE_CHANGED_OR_CANNOT_DECREASE" });
            }
            catch (ArgumentException) { return Results.BadRequest(new { code = "IDTRA_SEQUENCE_INVALID" }); }
        });

        // Resolves the current tenant on the server. The client never infers a
        // company from a user, a selected CRM account, or a hard-coded GUID.
        api.MapGet("/apertures/create-context", async (
            HttpContext http, OperationsCaseAccess access, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.case.create"))
                return Results.Forbid();
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            return Results.Ok(new { companyId = company });
        });

        api.MapPost("/cases", async (
            NewCaseRequest request, HttpContext http, CreateManualCaseHandler handler,
            OperationsCaseAccess access, OperationsCrmAccountResolver crm, OperationsOpeningCatalogValidator catalogValidator, AssignStageHandler originAssigner,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create") ||
                (!OperationsCaseAccess.IsSystemAdministrator(http.User) && company != request.CompanyId))
                return Results.Forbid();
            if (request.QuoteRateId.HasValue && !OperationsCaseAccess.HasScope(http.User, "operations.pricing.quote.link")) return Results.Forbid();
            if (request.QuoteRateId == Guid.Empty) return Results.BadRequest(new { code = "QUOTE_OPTION_REQUIRED" });
            if (request.AccountId == Guid.Empty) return Results.BadRequest(new { code = "CRM_ACCOUNT_REQUIRED" });
            var crmAccount = await crm.ResolveAsync(request.AccountId,
                http.Request.Headers.Authorization.ToString(), ct);
            if (crmAccount.ErrorCode is not null)
                return Results.Json(new { code = crmAccount.ErrorCode }, statusCode: crmAccount.StatusCode);
            try
            {
                var openingData = ValidateOpeningData(request.OpeningData);
                var opening = OperationsCargoMeasurements.Parse(openingData);
                var divisionResult = await crm.ValidateDivisionAsync(request.AccountId, opening,
                    http.Request.Headers.Authorization.ToString(), null, ct);
                if (divisionResult.ErrorCode is not null)
                    return Results.Json(new { code = divisionResult.ErrorCode }, statusCode: divisionResult.StatusCode);
                openingData = opening.ToJsonString();
                var missing = OperationsOpeningDraftValidator.MissingFields(request.ModalityCode, openingData, manualOpening: true);
                if (missing.Length > 0)
                    return Results.Conflict(new { code = "APERTURE_MISSING_REQUIRED_FIELDS", missingFields = missing });
                var catalogResult = await catalogValidator.ValidateAsync(openingData, http.Request.Headers.Authorization.ToString(), ct);
                if (catalogResult.ErrorCode is not null)
                    return Results.Json(new { code = catalogResult.ErrorCode, missingFields = catalogResult.Fields }, statusCode: catalogResult.StatusCode);
                var result = await handler.HandleAsync(
                    new CreateManualCaseCommand(request.CompanyId,
                        request.AccountId, crmAccount.ExecutiveUserId!.Value, request.ModalityCode,
                        request.Priority, request.ComplexityPoints, actor.Value,
                        request.OriginCountryCode, request.DestinationCountryCode,
                        openingData, SendDirectlyToOrigin: true, QuoteRateId: request.QuoteRateId), ct);
                // The persisted pending Origin stage is recovered by the durable worker
                // even if the immediate assignment fails. Never return failure after creation.
                try
                {
                    await originAssigner.HandleAsync(new AssignStageCommand(
                        result.Id, result.InitialStageId, actor.Value), ct);
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("OperationsManualOrigin").LogWarning(ex,
                        "Origin assignment pending for {CaseId}; worker will reconcile", result.Id);
                }
                return Results.Created($"/api/operations/cases/{result.Id}", result);
            }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });

        // Read current CRM operations contacts for the case's own customer account.
        // Never maintain a shadow copy of personal contact data in Operations.
        api.MapGet("/cases/{id:guid}/operations-contacts", async (
            Guid id, HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            IHttpClientFactory factory, IConfiguration configuration, CancellationToken ct, bool? directory) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.case.read") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var record = await db.Cases.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.AccountId, x.OpeningDataJson }).SingleOrDefaultAsync(ct);
            if (record is null) return Results.NotFound();
            var data = OperationsCargoMeasurements.Parse(record.OpeningDataJson);
            var query = directory == true ? "?includeDirectory=true" : "";
            if (directory != true && data["crmDivisionId"] is System.Text.Json.Nodes.JsonValue division &&
                division.TryGetValue<string>(out var divisionId) && !string.IsNullOrWhiteSpace(divisionId))
            {
                if (!Guid.TryParse(divisionId, out var parsed))
                    return Results.Conflict(new { code = "CRM_DIVISION_INVALID" });
                query = $"?divisionId={parsed:D}";
            }

            // This URI is deployment-owned; never accept an arbitrary URL from callers.
            var configured = configuration["Crm:ApiBaseUrl"];
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var baseUrl) ||
                (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrWhiteSpace(baseUrl.UserInfo))
                return Results.Problem(statusCode: 503, title: "CRM reference service is not configured");

            var actor = OperationsCaseAccess.UserId(http.User);
            var serviceKey = configuration["Crm:OperationsAcceptance:InternalServiceKey"];
            if (actor is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(serviceKey))
                return Results.Problem(statusCode: 503, title: "Internal CRM contacts key not configured");
            var url = new Uri(new Uri(baseUrl.ToString().TrimEnd('/') + "/"),
                $"api/internal/crm/operations/accounts/{record.AccountId:D}/contacts{query}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-Dhole-Service-Key", serviceKey);
            request.Headers.TryAddWithoutValidation("X-Dhole-Operations-Actor", actor.Value.ToString("D"));
            try
            {
                using var response = await factory.CreateClient("crm-operations-contacts")
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.Forbidden) return Results.Forbid();
                if (response.StatusCode == HttpStatusCode.Unauthorized) return Results.Unauthorized();
                if (response.StatusCode == HttpStatusCode.NotFound) return Results.NotFound();
                if (!response.IsSuccessStatusCode)
                    return Results.Problem(statusCode: 503, title: "CRM operations contacts temporarily unavailable");
                var body = await response.Content.ReadAsStringAsync(ct);
                return Results.Content(body, "application/json");
            }
            catch (HttpRequestException)
            {
                return Results.Problem(statusCode: 503, title: "CRM operations contacts temporarily unavailable");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Problem(statusCode: 503, title: "CRM operations contacts request timed out");
            }
        });

        api.MapGet("/cases/{id:guid}", async (
            Guid id, HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var result = await db.Cases.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new
                {
                    x.Id, x.Idtra, x.CompanyId, x.AccountId, x.ExecutiveUserId,
                    x.ModalityCode, x.OriginCountryCode, x.DestinationCountryCode,
                    x.ApertureType, x.SourceQuoteId, x.SourceQuoteOptionId,
                    x.Status, x.Priority, x.ComplexityPoints, x.CreatedBy, x.CreatedAtUtc,
                    x.UpdatedAtUtc, x.Version, x.OpeningDataJson,
                    Stages = x.Stages.Select(s => new { s.Id, s.DepartmentCode, s.Status, s.TeamId, s.Version })
                }).SingleAsync(ct);
            return Results.Ok(result);
        });

        // Server-side filtering is required: a page-local search hides authorized
        // IDTRAs on later pages and makes dashboard counters disagree with Trámites.
        api.MapGet("/cases", async (
            int? page, int? pageSize, string? q, string? status, string? apertureType, Guid? accountId,
            HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, OperationsAssignedUserLabels labels, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.read"))
                return Results.Forbid();
            var isAdmin = OperationsCaseAccess.IsSystemAdministrator(http.User);
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            if (!isAdmin && company is null) return Results.Forbid();
            if (q is { Length: > 120 } || status is { Length: > 40 } ||
                (apertureType is not null && apertureType is not ("" or "MANUAL" or "AUTOMATIC")))
                return Results.BadRequest(new { code = "OPERATIONS_CASE_FILTER_INVALID" });

            var size = Math.Clamp(pageSize ?? 25, 1, 100);
            var currentPage = Math.Max(1, page ?? 1);
            var query = access.FilterVisibleCases(db.Cases.AsNoTracking(), http.User, company);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = "%" + q.Trim().Replace("%", @"\%").Replace("_", @"\_") + "%";
                var lookup = q.Trim();
                var hasGuid = Guid.TryParse(lookup, out var guid);
                query = query.Where(c =>
                    EF.Functions.ILike(c.Idtra, pattern, @"\") ||
                    EF.Functions.ILike(c.ModalityCode, pattern, @"\") ||
                    (hasGuid && (c.AccountId == guid || c.SourceQuoteId == guid)) ||
                    db.CaseQuoteLinks.Any(link => link.CaseId == c.Id &&
                        link.QuoteNumber != null &&
                        EF.Functions.ILike(link.QuoteNumber, pattern, @"\")));
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                var value = status.Trim().ToUpperInvariant();
                query = query.Where(c => c.Status == value);
            }
            if (!string.IsNullOrWhiteSpace(apertureType))
                query = query.Where(c => c.ApertureType == apertureType);
            if (accountId.HasValue && accountId.Value != Guid.Empty)
                query = query.Where(c => c.AccountId == accountId.Value);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByIdtraDescending()
                .Skip((currentPage - 1) * size).Take(size).Select(c => new
                {
                    c.Id, c.Idtra, c.AccountId, c.ExecutiveUserId, c.ModalityCode,
                    c.OriginCountryCode, c.DestinationCountryCode,
                    c.ApertureType, c.SourceQuoteId, c.SourceQuoteOptionId,
                    QuoteNumber = db.CaseQuoteLinks.Where(q => q.CaseId == c.Id && q.IsPrimary)
                        .OrderByDescending(q => q.SnapshotVersion).Select(q => q.QuoteNumber).FirstOrDefault(),
                    c.Status, c.Priority, c.CreatedAtUtc, c.UpdatedAtUtc,
                    PendingValidatorIds = db.ApertureValidations.Where(v => v.CaseStage.CaseId == c.Id &&
                        v.Status == "PENDING" && v.CaseStage.Status == "PENDING_VALIDATION" &&
                        c.Status == "PENDING_VALIDATION").OrderBy(v => v.Slot).Take(1).Select(v => v.UserId).ToArray(),
                    DepartmentCode = c.Stages.OrderByDescending(s => s.Status == "IN_PROGRESS")
                        .ThenByDescending(s => s.Status == "READY")
                        .ThenByDescending(s => s.StartedAtUtc)
                        .Select(s => s.DepartmentCode).FirstOrDefault(),
                    DepartmentStatus = c.Stages.OrderByDescending(s => s.Status == "IN_PROGRESS")
                        .ThenByDescending(s => s.Status == "READY")
                        .ThenByDescending(s => s.StartedAtUtc)
                        .Select(s => s.Status).FirstOrDefault(),
                    TeamName = c.Stages.OrderByDescending(s => s.Status == "IN_PROGRESS")
                        .ThenByDescending(s => s.Status == "READY")
                        .ThenByDescending(s => s.StartedAtUtc)
                        .Select(s => db.Teams.Where(t => t.Id == s.TeamId)
                            .Select(t => t.Name).FirstOrDefault()).FirstOrDefault(),
                    AssignedUserId = c.Stages.OrderByDescending(s => s.Status == "IN_PROGRESS")
                        .ThenByDescending(s => s.Status == "READY")
                        .ThenByDescending(s => s.StartedAtUtc)
                        .Select(s => db.StageAssignments.Where(a =>
                            a.CaseStageId == s.Id && a.ReleasedAtUtc == null)
                            .Select(a => (Guid?)a.UserId).FirstOrDefault()).FirstOrDefault() ??
                        db.ApertureValidations.Where(v =>
                            v.CaseStage.CaseId == c.Id && v.Status == "PENDING")
                            .OrderBy(v => v.Slot).Select(v => (Guid?)v.UserId).FirstOrDefault(),
                    OriginStatusCode = db.OriginProgress.Where(p =>
                            p.CaseStage.CaseId == c.Id &&
                            p.CaseStage.DepartmentCode == "COORDINACION_ORIGEN")
                        .Select(p => p.StatusCode).FirstOrDefault()
                }).ToArrayAsync(ct);
            // The page is already case-authorized. Return only customer labels, never raw commercial JSON.
            var caseIds = items.Select(x => x.Id).ToArray();
            var savedNames = await db.AcceptedQuoteSnapshots.AsNoTracking()
                .Where(s => caseIds.Contains(s.CaseId))
                .OrderByDescending(s => s.CommercialRevision)
                .Select(s => new { s.CaseId, s.SnapshotJson }).ToArrayAsync(ct);
            var clientNames = savedNames.GroupBy(x => x.CaseId).ToDictionary(gr => gr.Key,
                gr => OperationsCommercialSnapshotReader.ClientName(gr.First().SnapshotJson));
            var people = await labels.ReadAsync(items.SelectMany(x => x.PendingValidatorIds)
                .Concat(items.Where(x => x.AssignedUserId.HasValue).Select(x => x.AssignedUserId!.Value)), ct);
            return Results.Ok(new { page = currentPage, pageSize = size, total, items, clientNames, people });
        });

        api.MapPost("/cases/{id:guid}/stages/{stageId:guid}/assign", async (
            Guid id, Guid stageId, HttpContext http,
            OperationsCaseAccess access, AssignStageHandler handler, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.assignment.manage") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            if (await db.CaseStages.AnyAsync(s => s.Id == stageId && s.CaseId == id &&
                s.DepartmentCode == "APERTURAS", ct))
                return Results.Conflict(new { code = "APERTURAS_REQUIRES_TWO_VALIDATORS" });
            var result = await handler.HandleAsync(
                new AssignStageCommand(id, stageId, actor.Value), ct);
            return result is null ? Results.Conflict(new { error = "Stage closed, already assigned, or no eligible pool candidate." })
                : Results.Ok(result);
        });

        api.MapPost("/teams", async (NewTeamRequest input, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.team.manage")) return Results.Forbid();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                await access.ResolveCompanyIdAsync(http.User, ct) != input.CompanyId)
                return Results.Forbid();
            if (input.CompanyId == Guid.Empty || string.IsNullOrWhiteSpace(input.Name) ||
                input.Name.Length > 150 || string.IsNullOrWhiteSpace(input.DepartmentCode) ||
                input.DepartmentCode.Length > 30) return Results.BadRequest();
            var team = new OperationsTeam
            {
                CompanyId = input.CompanyId, DepartmentCode = input.DepartmentCode.Trim().ToUpperInvariant(),
                Name = input.Name.Trim()
            };
            db.Teams.Add(team);
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null) return Results.Forbid();
            OperationsAuditWriter.Team(db, team.Id, actor.Value, "operations.team.created",
                null, new { team.Name, team.CompanyId, team.DepartmentCode });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/operations/teams/{team.Id}", new { team.Id });
        });

        api.MapGet("/teams", async (HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasTeamPermission(http.User, "operations.team.read")) return Results.Forbid();
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) && company is null) return Results.Forbid();
            var query = db.Teams.AsNoTracking();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User)) query = query.Where(x => x.CompanyId == company);
            var teams = await query.OrderBy(x => x.DepartmentCode).ThenBy(x => x.Name)
                .Select(x => new { x.Id, x.CompanyId, x.DepartmentCode, x.Name, x.IsActive,
                    Members = x.Members.Select(m => new { m.UserId, m.CapacityPoints, m.IsAvailable, m.IsSupervisor, m.RoleCode }) })
                .ToArrayAsync(ct);
            return Results.Ok(teams.Select(t => new { t.Id, t.CompanyId, t.DepartmentCode, t.Name, t.IsActive,
                t.Members, OperatorScopes = OperationsTeamCatalogEndpoints.OperatorScopes(t.DepartmentCode, t.Name),
                SupervisorScopes = OperationsTeamCatalogEndpoints.SupervisorScopes(t.DepartmentCode, t.Name),
                RoleProfiles = OperationsTeamCatalogEndpoints.RoleProfiles(t.DepartmentCode, t.Name) }));
        });

        api.MapPost("/teams/{teamId:guid}/members", async (
            Guid teamId, NewMemberRequest input, HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            OperationsAuthUserVerifier auth, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasTeamPermission(http.User, "operations.team.member.assign")) return Results.Forbid();
            var team = await db.Teams.FindAsync([teamId], ct);
            if (team is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) && team.CompanyId != await access.ResolveCompanyIdAsync(http.User, ct))
                return Results.Forbid();
            if (input.UserId == Guid.Empty || input.CapacityPoints <= 0 || input.CapacityPoints > 10000)
                return Results.BadRequest();
            try
            {
                if (!await auth.IsEligibleAsync(input.UserId,
                    http.Request.Headers.Authorization.ToString(), ct,
                    requireReview: false))
                    return Results.BadRequest(new { code = "AUTH_USER_NOT_ELIGIBLE" });
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (HttpRequestException) { return Results.Problem(statusCode: 503, title: "Auth eligibility unavailable"); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                { return Results.Problem(statusCode: 503, title: "Auth eligibility timed out"); }
            var member = await db.TeamMembers.FirstOrDefaultAsync(x => x.TeamId == teamId && x.UserId == input.UserId, ct);
            var role = OperationsTeamResponsibility.Resolve(input.RoleCode, input.IsSupervisor, member?.RoleCode);
            if (!OperationsTeamResponsibility.IsValid(role, team.DepartmentCode))
                return Results.BadRequest(new { code = "OPERATIONS_TEAM_ROLE_INVALID" });
            if ((OperationsTeamResponsibility.IsLeadership(role) || OperationsTeamResponsibility.IsLeadership(member?.RoleCode)) &&
                !OperationsCaseAccess.IsSystemAdministrator(http.User)) return Results.Forbid();
            var before = member is null ? null : new {
                member.CapacityPoints, member.IsAvailable, member.IsSupervisor, member.RoleCode
            };
            if (member is null)
            {
                member = new OperationsTeamMember { TeamId = teamId, UserId = input.UserId };
                db.TeamMembers.Add(member);
            }
            member.CapacityPoints = input.CapacityPoints;
            member.RoleCode = role;
            member.IsAvailable = OperationsTeamResponsibility.Available(role, input.IsAvailable);
            member.IsSupervisor = OperationsTeamResponsibility.IsSupervisor(role);
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null) return Results.Forbid();
            OperationsAuditWriter.Team(db, teamId, actor.Value,
                before is null ? "operations.team.member.added" : "operations.team.member.updated",
                before, new { member.UserId, member.CapacityPoints, member.IsAvailable, member.IsSupervisor, member.RoleCode });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { member.Id });
        });

        // All selected employees are checked by Auth BEFORE any write. One EF SaveChanges
        // transaction stores the members and their immutable AuditLogs Outbox events together.
        api.MapPost("/teams/{teamId:guid}/members/bulk", async (
            Guid teamId, NewMembersBatchRequest input, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db,
            OperationsAuthUserVerifier auth, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasTeamPermission(http.User, "operations.team.member.assign"))
                return Results.Forbid();
            var team = await db.Teams.SingleOrDefaultAsync(t => t.Id == teamId, ct);
            if (team is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                team.CompanyId != await access.ResolveCompanyIdAsync(http.User, ct))
                return Results.Forbid();

            var invalid = OperationsTeamMemberBatchPolicy.Validate(input.UserIds, input.CapacityPoints);
            if (invalid is not null) return Results.BadRequest(new { code = invalid });

            var role = OperationsTeamResponsibility.Resolve(input.RoleCode, input.IsSupervisor);
            if (!OperationsTeamResponsibility.IsValid(role, team.DepartmentCode))
                return Results.BadRequest(new { code = "OPERATIONS_TEAM_ROLE_INVALID" });
            if (OperationsTeamResponsibility.IsLeadership(role) &&
                !OperationsCaseAccess.IsSystemAdministrator(http.User)) return Results.Forbid();

            var ids = input.UserIds;
            var existing = await db.TeamMembers.AsNoTracking()
                .Where(m => m.TeamId == teamId && ids.Contains(m.UserId))
                .Select(m => m.UserId).ToArrayAsync(ct);
            var existingIds = existing.ToHashSet();
            var pending = ids.Where(id => !existingIds.Contains(id)).ToArray();
            try
            {
                // Even already assigned members must retain their Auth role eligibility;
                // the batch is fail-closed on stale/retired employees.
                foreach (var id in ids)
                    if (!await auth.IsEligibleAsync(id,
                        http.Request.Headers.Authorization.ToString(), ct))
                        return Results.BadRequest(new { code = "AUTH_USER_NOT_ELIGIBLE", userId = id });
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (HttpRequestException) {
                return Results.Problem(statusCode: 503, title: "Auth eligibility unavailable");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) {
                return Results.Problem(statusCode: 503, title: "Auth eligibility timed out");
            }

            foreach (var id in pending)
            {
                var member = new OperationsTeamMember {
                    TeamId = teamId, UserId = id, CapacityPoints = input.CapacityPoints,
                    RoleCode = role, IsSupervisor = OperationsTeamResponsibility.IsSupervisor(role),
                    IsAvailable = OperationsTeamResponsibility.Available(role, input.IsAvailable)
                };
                db.TeamMembers.Add(member);
                OperationsAuditWriter.Team(db, teamId, actor.Value,
                    "operations.team.member.added", null,
                    new { member.UserId, member.CapacityPoints, member.IsAvailable, member.IsSupervisor, member.RoleCode });
            }
            if (pending.Length > 0) await db.SaveChangesAsync(ct);
            return Results.Ok(new { assigned = pending.Length,
                skippedExisting = existingIds.Count, total = ids.Length });
        });

        api.MapPost("/assignment-rules", async (
            NewAssignmentRuleRequest input, HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.team.manage")) return Results.Forbid();
            var team = await db.Teams.FindAsync([input.TeamId], ct);
            if (team is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) && team.CompanyId != await access.ResolveCompanyIdAsync(http.User, ct))
                return Results.Forbid();
            if (!string.Equals(team.DepartmentCode, input.DepartmentCode, StringComparison.OrdinalIgnoreCase) ||
                input.ModalityCode?.Length > 30 || input.OriginCountryCode?.Length > 2 ||
                input.DestinationCountryCode?.Length > 2) return Results.BadRequest();
            var rule = new OperationsAssignmentRule
            {
                TeamId = team.Id, DepartmentCode = team.DepartmentCode, Priority = input.Priority,
                ModalityCode = input.ModalityCode?.Trim().ToUpperInvariant(),
                OriginCountryCode = input.OriginCountryCode?.Trim().ToUpperInvariant(),
                DestinationCountryCode = input.DestinationCountryCode?.Trim().ToUpperInvariant()
            };
            db.AssignmentRules.Add(rule);
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null) return Results.Forbid();
            OperationsAuditWriter.Team(db, team.Id, actor.Value,
                "operations.assignment-rule.created", null, new {
                    rule.Id, rule.DepartmentCode, rule.Priority, rule.ModalityCode,
                    rule.OriginCountryCode, rule.DestinationCountryCode
                });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/operations/assignment-rules/{rule.Id}", new { rule.Id });
        });

        api.MapPost("/cases/{id:guid}/documents", async (
            Guid id, NewDocumentRequest input, HttpContext http, OperationsCaseAccess access,
            RegisterDocumentHandler handler, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.document.manage") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            try
            {
                var result = await handler.HandleAsync(new RegisterDocumentCommand(
                    id, input.DocumentTypeCode, input.StorageFileId,
                    input.FileName, input.MimeType, input.Sha256, actor.Value), ct);
                return Results.Created($"/api/operations/cases/{id}/documents/{result.DocumentId}", result);
            }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });

        api.MapPost("/cases/{id:guid}/document-versions/{versionId:guid}/extractions", async (
            Guid id, Guid versionId, HttpContext http, OperationsCaseAccess access,
            QueueDocumentExtractionHandler handler, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.document.manage") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            try
            {
                var job = await handler.HandleAsync(
                    new QueueDocumentExtractionCommand(id, versionId, actor.Value), ct);
                return Results.Accepted($"/api/operations/cases/{id}/extraction-jobs", job);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        api.MapGet("/cases/{id:guid}/extraction-jobs", async (
            Guid id, HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var jobs = await db.ExtractionJobs.AsNoTracking().Where(x => x.DocumentVersion.Document.CaseId == id)
                .OrderByDescending(x => x.RequestedAtUtc)
                .Select(x => new { x.Id, x.DocumentVersionId, x.Status, x.RequestedAtUtc,
                    x.ReviewedAtUtc, x.ExternalExtractionJobId })
                .Take(100).ToArrayAsync(ct);
            return Results.Ok(jobs);
        });

        return app;
    }
}
