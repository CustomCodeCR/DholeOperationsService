using Dhole.Operations.Application.Cases;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Workers.Services;

/// <summary>
/// Reconciles the Aperturas→Origen handoff after both reviewers approve.
/// Independent of HTTP lifecycle: a crash/restart cannot lose an assignment.
/// Uses the existing rule-based, weighted, idempotent OperationsCaseStore.
/// </summary>
public sealed class OperationsOriginAssignmentWorker(
    IServiceScopeFactory scopes,
    ILogger<OperationsOriginAssignmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
                var pending = await db.CaseStages.AsNoTracking()
                    .Where(s => s.DepartmentCode == "COORDINACION_ORIGEN" &&
                        s.Status == "PENDING" && s.Case.Status == "APERTURA_VALIDADA")
                    .OrderBy(s => s.StartedAtUtc)
                    .Take(30)
                    .Select(s => new { s.CaseId, StageId = s.Id, s.Case.CreatedBy })
                    .ToArrayAsync(stoppingToken);
                if (pending.Length != 0)
                {
                    var store = new OperationsCaseStore(db);
                    foreach (var stage in pending)
                    {
                        try
                        {
                            var assigned = await store.AssignStageAsync(
                                new AssignStageCommand(
                                    stage.CaseId, stage.StageId, stage.CreatedBy),
                                stoppingToken);
                            if (assigned is not null)
                                logger.LogInformation(
                                    "Origin pool assigned case {CaseId} to verified user {UserId} on team {TeamId}",
                                    stage.CaseId, assigned.UserId, assigned.TeamId);
                            // No valid rule/member => stage remains PENDING. Never invent a user.
                        }
                        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                        {
                            logger.LogError(exception,
                                "Origin stage {StageId} remains pending, will retry",
                                stage.StageId);
                        }
                    }
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Origin handoff reconciliation cycle failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        }
    }
}
