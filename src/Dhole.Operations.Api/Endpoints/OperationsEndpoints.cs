using Dhole.Operations.Api.Security;
using Dhole.Operations.Application.Cases;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

public static class OperationsEndpoints
{
    public sealed record NewCaseRequest(
        Guid CompanyId, string SeriesCode, Guid AccountId, Guid ExecutiveUserId,
        string ModalityCode, short Priority = 1, decimal ComplexityPoints = 1m);

    public sealed record NewTeamRequest(Guid CompanyId, string DepartmentCode, string Name);
    public sealed record NewMemberRequest(Guid UserId, decimal CapacityPoints, bool IsSupervisor, bool IsAvailable = true);
    public sealed record NewAssignmentRuleRequest(
        Guid TeamId, string DepartmentCode, string? ModalityCode,
        string? OriginCountryCode, string? DestinationCountryCode, int Priority = 0);
    public sealed record AssignStageRequest(string? OriginCountryCode, string? DestinationCountryCode);
    public sealed record NewDocumentRequest(
        string DocumentTypeCode, Guid StorageFileId, string FileName, string MimeType, string? Sha256);

    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();

        api.MapPost("/cases", async (
            NewCaseRequest request, HttpContext http, CreateManualCaseHandler handler, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            var company = OperationsCaseAccess.CompanyId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.create") ||
                (!http.User.IsInRole("SuperAdmin") && company != request.CompanyId))
                return Results.Forbid();
            try
            {
                var result = await handler.HandleAsync(
                    new CreateManualCaseCommand(request.CompanyId, request.SeriesCode,
                        request.AccountId, request.ExecutiveUserId, request.ModalityCode,
                        request.Priority, request.ComplexityPoints, actor.Value), ct);
                return Results.Created($"/api/operations/cases/{result.Id}", result);
            }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });

        api.MapGet("/cases/{id:guid}", async (
            Guid id, HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var result = await db.Cases.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new
                {
                    x.Id, x.Idtra, x.CompanyId, x.AccountId, x.ExecutiveUserId,
                    x.ModalityCode, x.ApertureType, x.Status, x.Priority, x.CreatedAtUtc,
                    x.UpdatedAtUtc, x.Version,
                    Stages = x.Stages.Select(s => new { s.Id, s.DepartmentCode, s.Status, s.TeamId, s.Version })
                }).SingleAsync(ct);
            return Results.Ok(result);
        });

        api.MapGet("/cases", async (
            int? page, int? pageSize, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.case.read"))
                return Results.Forbid();
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null) return Results.Forbid();
            var size = Math.Clamp(pageSize ?? 25, 1, 100);
            var offset = (Math.Max(1, page ?? 1) - 1) * size;
            var query = db.Cases.AsNoTracking();
            if (!OperationsCaseAccess.HasScope(http.User, "operations.case.read-all"))
                query = query.Where(x => x.CreatedBy == actor.Value ||
                    x.Stages.Any(s => db.StageAssignments.Any(a =>
                        a.CaseStageId == s.Id && a.UserId == actor.Value && a.ReleasedAtUtc == null)) ||
                    x.Stages.Any(s => s.TeamId.HasValue &&
                        db.TeamMembers.Any(m => m.TeamId == s.TeamId && m.UserId == actor.Value && m.IsAvailable)));
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
                .Skip(offset).Take(size).Select(x => new
                {
                    x.Id, x.Idtra, x.AccountId, x.ModalityCode, x.Status, x.Priority, x.UpdatedAtUtc
                }).ToArrayAsync(ct);
            return Results.Ok(new { page = Math.Max(1, page ?? 1), pageSize = size, total, items });
        });

        api.MapPost("/cases/{id:guid}/stages/{stageId:guid}/assign", async (
            Guid id, Guid stageId, AssignStageRequest request, HttpContext http,
            OperationsCaseAccess access, AssignStageHandler handler, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.assignment.manage") ||
                !await access.CanReadAsync(id, http.User, ct)) return Results.Forbid();
            var result = await handler.HandleAsync(
                new AssignStageCommand(id, stageId, actor.Value,
                    request.OriginCountryCode, request.DestinationCountryCode), ct);
            return result is null ? Results.Conflict(new { error = "Stage closed, already assigned, or no eligible pool candidate." })
                : Results.Ok(result);
        });

        api.MapPost("/teams", async (NewTeamRequest input, HttpContext http,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.team.manage") ||
                (!http.User.IsInRole("SuperAdmin") && OperationsCaseAccess.CompanyId(http.User) != input.CompanyId))
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
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/operations/teams/{team.Id}", new { team.Id });
        });

        api.MapGet("/teams", async (HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.team.manage")) return Results.Forbid();
            var company = OperationsCaseAccess.CompanyId(http.User);
            if (!http.User.IsInRole("SuperAdmin") && company is null) return Results.Forbid();
            var query = db.Teams.AsNoTracking();
            if (!http.User.IsInRole("SuperAdmin")) query = query.Where(x => x.CompanyId == company);
            return Results.Ok(await query.OrderBy(x => x.DepartmentCode).ThenBy(x => x.Name)
                .Select(x => new { x.Id, x.CompanyId, x.DepartmentCode, x.Name, x.IsActive,
                    Members = x.Members.Select(m => new { m.UserId, m.CapacityPoints, m.IsAvailable, m.IsSupervisor }) })
                .ToArrayAsync(ct));
        });

        api.MapPost("/teams/{teamId:guid}/members", async (
            Guid teamId, NewMemberRequest input, HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.team.manage")) return Results.Forbid();
            var team = await db.Teams.FindAsync([teamId], ct);
            if (team is null) return Results.NotFound();
            if (!http.User.IsInRole("SuperAdmin") && team.CompanyId != OperationsCaseAccess.CompanyId(http.User))
                return Results.Forbid();
            if (input.UserId == Guid.Empty || input.CapacityPoints <= 0 || input.CapacityPoints > 10000)
                return Results.BadRequest();
            var member = await db.TeamMembers.FirstOrDefaultAsync(x => x.TeamId == teamId && x.UserId == input.UserId, ct);
            if (member is null)
            {
                member = new OperationsTeamMember { TeamId = teamId, UserId = input.UserId };
                db.TeamMembers.Add(member);
            }
            member.CapacityPoints = input.CapacityPoints;
            member.IsAvailable = input.IsAvailable;
            member.IsSupervisor = input.IsSupervisor;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { member.Id });
        });

        api.MapPost("/assignment-rules", async (
            NewAssignmentRuleRequest input, HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.team.manage")) return Results.Forbid();
            var team = await db.Teams.FindAsync([input.TeamId], ct);
            if (team is null) return Results.NotFound();
            if (!http.User.IsInRole("SuperAdmin") && team.CompanyId != OperationsCaseAccess.CompanyId(http.User))
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
