using Dhole.Operations.Application.Cases;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Workers.Services;

/// <summary>
/// Durable reconciliation of the validated Aperturas -> Origen handoff.
/// The API attempts the assignment immediately, while this worker recovers
/// crashes and pool changes without duplicating a stage or an assignment.
/// A missing eligible team leaves the stage pending for supervisor action.
/// </summary>
public sealed class OperationsOriginAssignmentWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OperationsOriginAssignmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
                var candidates = await db.CaseStages.AsNoTracking()
                    .Where(stage => stage.DepartmentCode == "COORDINACION_ORIGEN"
                        && stage.Status == "PENDING"
                        && (stage.Case.Status == "APERTURA_VALIDADA" ||
                            (stage.Case.ApertureType == "MANUAL" && stage.Case.Status == "OPEN")))
                    .OrderBy(stage => stage.StartedAtUtc)
                    .ThenBy(stage => stage.Id)
                    .Select(stage => new { stage.Id, stage.CaseId, stage.Case.ApertureType, stage.Case.CreatedBy })
                    .ToArrayAsync(stoppingToken);

                foreach (var stage in candidates)
                {
                    // The final validator is the actual author of the transition.
                    // Do not fabricate a shared Operations user for the worker.
                    var actor = stage.ApertureType == "MANUAL" ? stage.CreatedBy : await db.ApertureValidations.AsNoTracking()
                        .Where(validation =>
                            validation.CaseStage.CaseId == stage.CaseId
                            && validation.Status == "APPROVED")
                        .OrderByDescending(validation => validation.ReviewedAtUtc)
                        .Select(validation => validation.UserId)
                        .FirstOrDefaultAsync(stoppingToken);
                    if (actor == Guid.Empty) continue;
                    try
                    {
                        // PostgreSQL advisory locking and the partial unique
                        // index on the active stage assignment ensure idempotence.
                        using var assignmentScope = scopeFactory.CreateScope();
                        var caseAssigner = assignmentScope.ServiceProvider.GetRequiredService<AssignStageHandler>();
                        await caseAssigner.HandleAsync(
                            new AssignStageCommand(stage.CaseId, stage.Id, actor),
                            stoppingToken);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogError(error,
                            "Unable to reconcile Origin pool for case {CaseId}", stage.CaseId);
                    }
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Origin assignment reconciliation cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
