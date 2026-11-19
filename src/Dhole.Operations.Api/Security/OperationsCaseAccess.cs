using System.Security.Claims;
using Dhole.Operations.Persistence;
using Dhole.Operations.Domain.Services;
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

    public static bool IsSystemAdministrator(ClaimsPrincipal user) =>
        user.IsInRole("SuperAdmin") || user.IsInRole("SuperUsuario") ||
        user.IsInRole("SuperUser") || user.IsInRole("superusuario");

    public static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.Identity?.IsAuthenticated == true &&
        (IsSystemAdministrator(user) ||
         user.Claims.Where(x => x.Type is "scope" or "scp" or "scopes")
             .SelectMany(x => x.Value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
             .Contains(scope, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Auth currently issues internal-user JWTs without a company identifier.
    /// Accept such a context only when the Operations database has precisely one
    /// company; never turn a missing company claim into multi-tenant access.
    /// </summary>
    public async Task<Guid?> ResolveCompanyIdAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var claimed = CompanyId(user);
        if (claimed.HasValue && claimed.Value != Guid.Empty) return claimed;
        var known = await db.Teams.AsNoTracking().Select(x => x.CompanyId)
            .Union(db.Cases.AsNoTracking().Select(x => x.CompanyId))
            .Distinct().Take(2).ToArrayAsync(ct);
        return OperationsCompanyBootstrapSelector.ResolveExisting(claimed, known);
    }

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
            db.ApertureValidations.Any(v => v.CaseStage.CaseId == x.Id && v.UserId == actor.Value) ||
            x.Stages.Any(stage => db.StageAssignments.Any(assignment =>
                assignment.CaseStageId == stage.Id &&
                assignment.UserId == actor.Value && assignment.ReleasedAtUtc == null)) ||
            x.Stages.Any(stage => stage.TeamId.HasValue &&
                db.TeamMembers.Any(m => m.TeamId == stage.TeamId && m.UserId == actor.Value)), ct);
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
                    db.TeamMembers.Any(m => m.TeamId == stage.TeamId && m.UserId == actor.Value)));
        return await query.Select(x => x.Id).ToArrayAsync(ct);
    }
}
