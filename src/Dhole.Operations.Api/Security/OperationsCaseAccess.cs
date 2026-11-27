using System.Security.Claims;
using Dhole.Operations.Persistence;
using Dhole.Operations.Domain.Models;
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
        user.Identity?.IsAuthenticated == true &&
        (user.IsInRole("SuperAdmin") || user.IsInRole("SuperUsuario") ||
         user.IsInRole("SuperUser") ||
         user.Claims.Where(c => c.Type is ClaimTypes.Role or "role" or "roles")
             .SelectMany(c => c.Value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
             .Any(role => role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase) ||
                          role.Equals("SuperUsuario", StringComparison.OrdinalIgnoreCase) ||
                          role.Equals("SuperUser", StringComparison.OrdinalIgnoreCase)));

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

    // One SQL predicate for list, counts, direct REST reads and SignalR joins.
    // An ordinary member needs BOTH the current team membership and a personal
    // assignment. Only a supervisor of that same team can see its other cases.
    public IQueryable<OperationsCase> FilterVisibleCases(IQueryable<OperationsCase> query,
        ClaimsPrincipal user, Guid? company, Guid? teamId = null, string? department = null)
    {
        var actor = UserId(user);
        if (actor is null || !HasScope(user, "operations.case.read"))
            return query.Where(c => false);
        var admin = IsSystemAdministrator(user);
        if (!admin)
        {
            if (company is null) return query.Where(c => false);
            query = query.Where(c => c.CompanyId == company.Value);
        }
        if (admin && teamId is null && department is null) return query;
        var allScope = !admin && HasScope(user, "operations.case.read-all");
        var originScope = !admin && HasScope(user, "operations.origin.read-all");
        // Scope claims alone are insufficient: active role membership and company
        // must still match in SQL, including during token refresh/revocation.
        return query.Where(c =>
            (allScope && db.TeamMembers.Any(m => m.UserId == actor.Value && m.Team.IsActive &&
                m.Team.CompanyId == c.CompanyId &&
                (m.RoleCode == OperationsTeamResponsibility.LogisticsManager || m.RoleCode == OperationsTeamResponsibility.OperationsHead)) &&
                ((!teamId.HasValue && department == null) || c.Stages.Any(s =>
                    (!teamId.HasValue || s.TeamId == teamId) && (department == null || s.DepartmentCode == department)))) ||
            (c.Status != "CANCELLED" && c.Status != "COMPLETED" &&
            c.Stages.Any(s => s.Status != "COMPLETED" && s.Status != "SKIPPED" &&
                (!teamId.HasValue || s.TeamId == teamId) &&
                (department == null || s.DepartmentCode == department) &&
                (admin || (originScope && s.DepartmentCode == "COORDINACION_ORIGEN" &&
                    db.TeamMembers.Any(m => m.UserId == actor.Value && m.Team.IsActive &&
                        m.Team.CompanyId == c.CompanyId && m.Team.DepartmentCode == "COORDINACION_ORIGEN" &&
                        m.RoleCode == OperationsTeamResponsibility.OriginSupervisor)) || (s.TeamId.HasValue &&
                    db.TeamMembers.Any(m => m.TeamId == s.TeamId.Value &&
                        m.UserId == actor.Value && m.Team.IsActive &&
                        (m.IsSupervisor ||
                         ((s.DepartmentCode != "APERTURAS" || c.ApertureType != "AUTOMATIC") &&
                          db.StageAssignments.Any(a => a.CaseStageId == s.Id &&
                             a.TeamId == m.TeamId && a.UserId == actor.Value && a.ReleasedAtUtc == null)) ||
                         (s.DepartmentCode == "APERTURAS" && c.ApertureType == "AUTOMATIC" &&
                          db.ApertureValidations.Any(v => v.CaseStageId == s.Id &&
                             v.TeamId == m.TeamId && v.UserId == actor.Value &&
                             ((s.Status == "PENDING_VALIDATION" && v.Status == "PENDING" &&
                               !db.ApertureValidations.Any(previous => previous.CaseStageId == v.CaseStageId && previous.Status == "PENDING" && previous.Slot < v.Slot)) ||
                              (s.Status == "CHANGES_REQUIRED" && v.Status == "REJECTED")))))))))));
    }

    public async Task<bool> CanReadAsync(Guid caseId, ClaimsPrincipal user, CancellationToken ct)
    {
        var company = IsSystemAdministrator(user) ? null : await ResolveCompanyIdAsync(user, ct);
        return await FilterVisibleCases(db.Cases.AsNoTracking().Where(c => c.Id == caseId),
            user, company).AnyAsync(ct);
    }

    public async Task<Guid[]> AccessibleCaseIdsAsync(Guid[] caseIds, ClaimsPrincipal user, CancellationToken ct)
    {
        var company = IsSystemAdministrator(user) ? null : await ResolveCompanyIdAsync(user, ct);
        return await FilterVisibleCases(db.Cases.AsNoTracking().Where(c => caseIds.Contains(c.Id)),
            user, company).Select(c => c.Id).ToArrayAsync(ct);
    }
}
