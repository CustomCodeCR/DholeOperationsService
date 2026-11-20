using Dhole.Operations.Api.Security;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// Team-aware Aperturas overview. The server applies the same case authorization as
/// the validation queue before counting or returning any record.
/// </summary>
public static class OperationsDashboardEndpoints
{
    public static IEndpointRouteBuilder MapOperationsDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/operations/dashboard", async (
            Guid? teamId, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.read"))
                return Results.Forbid();

            var isAdmin = OperationsCaseAccess.IsSystemAdministrator(http.User);
            var readAll = OperationsCaseAccess.HasScope(http.User, "operations.case.read-all");
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            if (!isAdmin && company is null) return Results.Forbid();

            var teamQuery = db.Teams.AsNoTracking();
            if (!isAdmin) teamQuery = teamQuery.Where(t => t.CompanyId == company);
            if (!readAll)
                teamQuery = teamQuery.Where(t => t.Members.Any(m => m.UserId == actor.Value));

            var teams = await teamQuery.OrderBy(t => t.DepartmentCode).ThenBy(t => t.Name)
                .Select(t => new
                {
                    t.Id, t.Name, t.DepartmentCode, t.IsActive,
                    IsSupervisor = t.Members.Any(m => m.UserId == actor.Value && m.IsSupervisor)
                }).ToArrayAsync(ct);

            // A forged teamId must never expose another team's cases or aggregate counts.
            if (teamId.HasValue && !teams.Any(t => t.Id == teamId.Value))
                return Results.Forbid();

            var query = db.Cases.AsNoTracking()
                .Where(c => c.Stages.Any(s => s.DepartmentCode == "APERTURAS"));
            if (!isAdmin) query = query.Where(c => c.CompanyId == company);
            if (!readAll)
                query = query.Where(c =>
                    c.CreatedBy == actor.Value ||
                    db.ApertureValidations.Any(v => v.CaseStage.CaseId == c.Id && v.UserId == actor.Value) ||
                    c.Stages.Any(s => s.TeamId.HasValue && db.TeamMembers.Any(m =>
                        m.TeamId == s.TeamId && m.UserId == actor.Value)) ||
                    c.Stages.Any(s => db.StageAssignments.Any(a =>
                        a.CaseStageId == s.Id && a.UserId == actor.Value && a.ReleasedAtUtc == null)));

            if (teamId.HasValue)
                query = query.Where(c =>
                    c.Stages.Any(s => s.DepartmentCode == "APERTURAS" && s.TeamId == teamId.Value) ||
                    db.ApertureValidations.Any(v =>
                        v.CaseStage.CaseId == c.Id && v.TeamId == teamId.Value));

            var total = await query.CountAsync(ct);
            var manual = await query.CountAsync(c => c.ApertureType == "MANUAL", ct);
            var automatic = await query.CountAsync(c => c.ApertureType == "AUTOMATIC", ct);
            var awaitingAssignment = await query.CountAsync(c =>
                !db.ApertureValidations.Any(v => v.CaseStage.CaseId == c.Id), ct);
            var inReview = await query.CountAsync(c =>
                db.ApertureValidations.Any(v =>
                    v.CaseStage.CaseId == c.Id && v.Status == "PENDING"), ct);
            var approved = await query.CountAsync(c =>
                db.ApertureValidations.Count(v =>
                    v.CaseStage.CaseId == c.Id && v.Status == "APPROVED") >= 2, ct);

            var items = await query
                .OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.Id)
                .Take(30)
                .Select(c => new
                {
                    c.Id, c.Idtra, c.ModalityCode, c.Status, c.Priority,
                    c.ApertureType, c.SourceQuoteId,
                    c.CreatedAtUtc,
                    TeamId = c.Stages.Where(s => s.DepartmentCode == "APERTURAS")
                        .Select(s => s.TeamId).FirstOrDefault(),
                    ApprovedReviews = db.ApertureValidations.Count(v =>
                        v.CaseStage.CaseId == c.Id && v.Status == "APPROVED"),
                    PendingReviews = db.ApertureValidations.Count(v =>
                        v.CaseStage.CaseId == c.Id && v.Status == "PENDING")
                }).ToArrayAsync(ct);

            return Results.Ok(new
            {
                teams,
                stats = new { total, manual, automatic, awaitingAssignment, inReview, approved },
                items
            });
        }).RequireAuthorization();

        return app;
    }
}
