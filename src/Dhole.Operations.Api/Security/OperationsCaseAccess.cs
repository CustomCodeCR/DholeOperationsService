using System.Security.Claims;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Security;

/// <summary>Every REST and SignalR read passes through the same case authorization predicate.</summary>
public sealed class OperationsCaseAccess(OperationsDbContext db)
{
    public static Guid? UserId(ClaimsPrincipal user)
    {
        var value = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public static Guid? CompanyId(ClaimsPrincipal user)
    {
        var value = user.FindFirst("company_id")?.Value ?? user.FindFirst("companyId")?.Value
            ?? user.FindFirst("tenant_id")?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.Identity?.IsAuthenticated == true &&
        (user.IsInRole("SuperAdmin") ||
         user.Claims.Where(x => x.Type is "scope" or "scp" or "scopes")
             .SelectMany(x => x.Value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
             .Contains(scope, StringComparer.OrdinalIgnoreCase));

    public async Task<bool> CanReadAsync(Guid caseId, ClaimsPrincipal user, CancellationToken ct)
    {
        var actor = UserId(user);
        if (actor is null || !HasScope(user, "operations.case.read"))
            return false;
        var caseQuery = db.Cases.AsNoTracking().Where(x => x.Id == caseId);
        if (HasScope(user, "operations.case.read-all"))
            return await caseQuery.AnyAsync(ct);
        return await caseQuery.AnyAsync(x =>
            x.CreatedBy == actor.Value ||
            x.Stages.Any(stage => db.StageAssignments.Any(assignment =>
                assignment.CaseStageId == stage.Id &&
                assignment.UserId == actor.Value && assignment.ReleasedAtUtc == null)) ||
            x.Stages.Any(stage => stage.TeamId.HasValue &&
                db.TeamMembers.Any(m => m.TeamId == stage.TeamId && m.UserId == actor.Value &&
                    m.IsAvailable)), ct);
    }

    public async Task<Guid[]> AccessibleCaseIdsAsync(Guid[] caseIds, ClaimsPrincipal user, CancellationToken ct)
    {
        var actor = UserId(user);
        if (actor is null || !HasScope(user, "operations.case.read")) return [];
        var query = db.Cases.AsNoTracking().Where(x => caseIds.Contains(x.Id));
        if (!HasScope(user, "operations.case.read-all"))
            query = query.Where(x => x.CreatedBy == actor.Value ||
                x.Stages.Any(stage => db.StageAssignments.Any(assignment =>
                    assignment.CaseStageId == stage.Id &&
                    assignment.UserId == actor.Value && assignment.ReleasedAtUtc == null)) ||
                x.Stages.Any(stage => stage.TeamId.HasValue &&
                    db.TeamMembers.Any(m => m.TeamId == stage.TeamId && m.UserId == actor.Value && m.IsAvailable)));
        return await query.Select(x => x.Id).ToArrayAsync(ct);
    }
}
