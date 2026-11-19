using System.Data;
using System.Text.Json;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Persistence.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>
/// Idempotent, tenant-scoped creation of the standard operations team catalog.
/// This endpoint NEVER creates user accounts, memberships, supervisors or grants.
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
        ("DOCUMENTACION", "Documentación"),
        ("TRANSITO", "Tránsito"),
        ("PANAMA", "Operaciones Panamá"),
        ("COSTA_RICA", "Operaciones Costa Rica"),
        ("FINANZAS", "Finanzas")
    ];

    public static IEndpointRouteBuilder MapOperationsTeamCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/operations/teams/initialize", async (
            HttpContext http, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            var company = OperationsCaseAccess.CompanyId(http.User);
            if (actor is null || company is null || company == Guid.Empty ||
                !OperationsCaseAccess.HasScope(http.User, "operations.team.manage"))
                return Results.Forbid();

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
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

                // A default Aperturas pool requires two active, individually authorized reviewers.
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

                // Only US-specific routing is unambiguous in the current rule schema.
                // Do not create a catch-all international route that could capture US cases.
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
