using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// Idempotent, tenant-scoped creation of the standard operations team catalog.
/// This endpoint creates only teams; active memberships are synchronized to Auth separately.
/// </summary>
public static class OperationsTeamCatalogEndpoints
{
    public static readonly (string Department, string Name)[] StandardTeams =
    [
        ("APERTURAS", "Aperturas"),
        ("COORDINACION_ORIGEN", "Origen · Estados Unidos"),
        ("COORDINACION_ORIGEN", "Origen · FCL internacional"),
        ("COORDINACION_ORIGEN", "Origen · LCL y Aéreo internacional"),
        ("COORDINACION_ORIGEN", "Origen · Transporte terrestre"),
        ("COORDINACION_ORIGEN", OperationsOriginRouting.CentralAmericaTeam),
        ("COORDINACION_ORIGEN", OperationsOriginRouting.TransitTeam),
        ("COORDINACION_ORIGEN", OperationsOriginRouting.FtlTeam),
        ("COORDINACION_ORIGEN", OperationsOriginRouting.LtlTeam),
        ("DOCUMENTACION", "Documentación"),
        ("TRANSITO", "Tránsito"),
        ("PANAMA", "Operaciones Panamá"),
        ("COSTA_RICA", "Operaciones Costa Rica"),
        ("FINANZAS", "Finanzas")
    ];

    // Profiles mirror Auth's allowlisted automatic membership grants. Future departments
    // have no default grants until their business rules are defined.
    public static string[] OperatorScopes(string department, string? teamName = null) => department switch
    {
        "APERTURAS" => ["operations.case.read", "operations.case.create", "operations.case.update",
            "operations.aperture.update", "operations.aperture.validation.review", "operations.realtime.read"],
        "COORDINACION_ORIGEN" => ["operations.case.read", "operations.origin.read", "operations.origin.update",
            "operations.origin.departure.confirm", "operations.internal-note.read",
            "operations.internal-note.create", "operations.document.manage", "operations.realtime.read",
            .. (teamName == OperationsOriginRouting.CentralAmericaTeam ? new[] { "operations.central-america.manage" } :
                teamName is "Origen · Transporte terrestre" or OperationsOriginRouting.FtlTeam or OperationsOriginRouting.LtlTeam ? new[] { "operations.land.manage" } : Array.Empty<string>())],
        "DOCUMENTACION" => ["operations.case.read", "operations.document.manage", "operations.realtime.read"],
        "TRANSITO" => ["operations.case.read", "operations.transit.manage", "operations.realtime.read"],
        "PANAMA" => ["operations.case.read", "operations.panama.manage", "operations.realtime.read"],
        "COSTA_RICA" => ["operations.case.read", "operations.costa-rica.manage", "operations.realtime.read"],
        _ => []
    };

    public static string[] SupervisorScopes(string department, string? teamName = null) => department switch
    {
        "APERTURAS" or "COORDINACION_ORIGEN" or "DOCUMENTACION" or "TRANSITO" or "PANAMA" or "COSTA_RICA" => [.. OperatorScopes(department, teamName), "operations.audit.read"],
        _ => []
    };

    public static Dictionary<string, string[]> RoleProfiles(string department, string? teamName = null)
    {
        var profiles = new Dictionary<string, string[]>
        {
            [OperationsTeamResponsibility.Operator] = OperatorScopes(department, teamName),
            [OperationsTeamResponsibility.LegacyTeamSupervisor] = SupervisorScopes(department, teamName)
        };
        if (OperationsTeamResponsibility.IsValid(OperationsTeamResponsibility.LogisticsManager, department))
        {
            profiles[OperationsTeamResponsibility.LogisticsManager] = ["operations.case.read", "operations.case.read-all", "operations.realtime.read"];
            profiles[OperationsTeamResponsibility.OperationsHead] = ["operations.case.read", "operations.case.read-all", "operations.realtime.read"];
        }
        if (department == "COORDINACION_ORIGEN")
            profiles[OperationsTeamResponsibility.OriginSupervisor] = ["operations.case.read", "operations.origin.read", "operations.origin.read-all", "operations.realtime.read"];
        return profiles;
    }

    public static IEndpointRouteBuilder MapOperationsTeamCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/operations/teams/initialize", async (
            HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.team.manage"))
                return Results.Forbid();

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            // Serializes first-run provisioning before a tenant identity is persisted.
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(839162097424);", ct);
            var knownCompanies = await db.Teams.AsNoTracking().Select(x => x.CompanyId)
                .Union(db.Cases.AsNoTracking().Select(x => x.CompanyId))
                .Distinct().Take(2).ToArrayAsync(ct);
            var company = OperationsCompanyBootstrapSelector.ResolveForBootstrap(
                OperationsCaseAccess.CompanyId(http.User),
                OperationsCaseAccess.IsSystemAdministrator(http.User),
                knownCompanies, Guid.NewGuid());
            if (company is null)
                return Results.Conflict(new {
                    code = "OPERATIONS_COMPANY_CONTEXT_REQUIRED",
                    message = "La empresa operativa no se puede identificar de forma segura. Seleccione una empresa o solicite a un SuperUsuario que inicialice el entorno."
                });

            var lockKey = "operations-team-catalog:" + company.Value.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));", ct);

            var existing = await db.Teams.Where(t => t.CompanyId == company.Value).ToListAsync(ct);
            var created = 0;
            foreach (var template in StandardTeams)
            {
                var team = existing.FirstOrDefault(t => t.DepartmentCode == template.Department &&
                    string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase));
                if (team is null)
                {
                    team = new OperationsTeam {
                        CompanyId = company.Value, DepartmentCode = template.Department,
                        Name = template.Name, IsActive = true
                    };
                    db.Teams.Add(team);
                    existing.Add(team);
                    OperationsAuditWriter.Team(db, team.Id, actor.Value,
                        "operations.team.catalog.created", null,
                        new { team.CompanyId, team.DepartmentCode, team.Name });
                    created++;
                }

                // A default Aperturas pool requires an active, individually authorized reviewer.
                // Respect manually configured rules: never replace or broaden an existing rule.
                if (template.Department == "APERTURAS" &&
                    !await db.AssignmentRules.AnyAsync(r => r.TeamId == team.Id, ct))
                {
                    db.AssignmentRules.Add(new OperationsAssignmentRule {
                        TeamId = team.Id, DepartmentCode = template.Department, Priority = 100
                    });
                    OperationsAuditWriter.Team(db, team.Id, actor.Value,
                        "operations.assignment-rule.catalog.created", null,
                        new { team.Id, department = template.Department, priority = 100 });
                }

                // International defaults are evaluated only after country-specific routing.
                // The assignment engine explicitly excludes US and unknown origins from these defaults.
                if (template.Name is "Origen · FCL internacional" or "Origen · LCL y Aéreo internacional" &&
                    !await db.AssignmentRules.AnyAsync(r => r.TeamId == team.Id, ct))
                {
                    var modalities = template.Name == "Origen · FCL internacional" ? new[] { "FCL" } : new[] { "LCL", "AIR" };
                    foreach (var modality in modalities)
                        db.AssignmentRules.Add(new OperationsAssignmentRule {
                            TeamId = team.Id, DepartmentCode = template.Department, ModalityCode = modality, Priority = 50
                        });
                    OperationsAuditWriter.Team(db, team.Id, actor.Value,
                        "operations.assignment-rule.catalog.created", null, new { team.Id, modalities, priority = 50 });
                }
                if (template.Name == "Origen · Estados Unidos" &&
                    !await db.AssignmentRules.AnyAsync(r => r.TeamId == team.Id, ct))
                {
                    foreach (var modality in new[] { "FCL", "LCL", "AIR" })
                        db.AssignmentRules.Add(new OperationsAssignmentRule {
                            TeamId = team.Id, DepartmentCode = template.Department,
                            OriginCountryCode = "US", ModalityCode = modality, Priority = 100
                        });
                    OperationsAuditWriter.Team(db, team.Id, actor.Value,
                        "operations.assignment-rule.catalog.created", null,
                        new { team.Id, originCountryCode = "US", modalities = new[] { "FCL", "LCL", "AIR" } });
                }
            }

            if (db.ChangeTracker.HasChanges())
            {
                db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                    "operations.team.catalog.initialized.v1",
                    JsonSerializer.Serialize(new { companyId = company.Value, created })));
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { created, total = existing.Count });
        }).RequireAuthorization();
        return app;
    }
}
