using System.Text.Json;
using Dhole.Operations.Persistence.Messaging;
using Dhole.Operations.Application.Cases;
using System.Data;
using Dhole.Operations.Persistence.Audit;
using Dhole.Operations.Api.Integrations;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Domain.Models;
using Dhole.Operations.Domain.Services;
using Dhole.Operations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Api.Endpoints;

/// <summary>Two independent, individually audited Aperturas validation decisions per case.</summary>
public static class OperationsTeamValidationEndpoints
{
    public sealed record AssignReviewersRequest(Guid? TeamId);
    public sealed record ReviewDecisionRequest(string Decision, string? Comment);

    public static IEndpointRouteBuilder MapOperationsTeamValidationEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization();
        api.MapGet("/apertures/validation-queue", async (
            HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            int? page, int? pageSize, string? q, string? status, string? apertureType,
            CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.case.read"))
                return Results.Forbid();
            var company = await access.ResolveCompanyIdAsync(http.User, ct);
            var readAll = OperationsCaseAccess.HasScope(http.User, "operations.case.read-all");
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) && company is null) return Results.Forbid();
            var query = db.Cases.AsNoTracking()
                .Where(c => c.Stages.Any(s => s.DepartmentCode == "APERTURAS"));
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User))
                query = query.Where(c => c.CompanyId == company);
            if (!readAll)
                query = query.Where(c => c.CreatedBy == actor.Value ||
                    c.Stages.Any(s => db.ApertureValidations.Any(v =>
                        v.CaseStageId == s.Id && v.UserId == actor.Value)) ||
                    c.Stages.Any(s => s.TeamId.HasValue && db.TeamMembers.Any(m =>
                        m.TeamId == s.TeamId && m.UserId == actor.Value)));
            if (q is { Length: > 120 } || status is { Length: > 40 } ||
                (apertureType is not null && apertureType is not ("" or "MANUAL" or "AUTOMATIC")))
                return Results.BadRequest(new { code = "OPERATIONS_APERTURE_FILTER_INVALID" });
            if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = "%" + q.Trim().Replace("%", @"\%").Replace("_", @"\_") + "%";
                query = query.Where(c => EF.Functions.ILike(c.Idtra, pattern, @"\"));
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                var value = status.Trim().ToUpperInvariant();
                query = query.Where(c => c.Status == value);
            }
            if (!string.IsNullOrWhiteSpace(apertureType))
                query = query.Where(c => c.ApertureType == apertureType);
            var n = Math.Max(page ?? 1, 1);
            var size = Math.Clamp(pageSize ?? 25, 1, 100);
            return Results.Ok(new {
                page = n, pageSize = size, total = await query.CountAsync(ct),
                items = await query.OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.Id)
                    .Skip((n - 1) * size).Take(size).Select(c => new {
                        c.Id, c.Idtra, c.CompanyId, c.AccountId, c.ModalityCode,
                        c.Status, c.Priority, c.ApertureType, c.SourceQuoteId, c.SourceQuoteOptionId,
                        c.CreatedAtUtc,
                        Validators = db.ApertureValidations.Where(v => v.CaseStage.CaseId == c.Id)
                            .OrderBy(v => v.Slot)
                            .Select(v => new { v.Id, v.Slot, v.UserId, v.Status }).ToArray()
                    }).ToArrayAsync(ct)
            });
        });

        api.MapGet("/cases/{caseId:guid}/aperture-validations", async (
            Guid caseId, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            var stage = await db.CaseStages.AsNoTracking()
                .Where(s => s.CaseId == caseId && s.DepartmentCode == "APERTURAS")
                .Select(s => new { s.Id, s.Status, s.TeamId }).SingleOrDefaultAsync(ct);
            if (stage is null) return Results.NotFound();
            var validators = await db.ApertureValidations.AsNoTracking()
                .Where(v => v.CaseStageId == stage.Id).OrderBy(v => v.Slot)
                .Select(v => new { v.Id, v.TeamId, v.UserId, v.Slot, v.Status,
                    v.AssignedAtUtc, v.ReviewedAtUtc, v.Comment }).ToArrayAsync(ct);
            return Results.Ok(new { stage, requiredReviewers = 2, validators });
        });

        // Safe, company-scoped team choices for a manual opening. Return only
        // aggregates; never expose individual Auth identities to form creators.
        api.MapGet("/apertures/assignment-teams", async (
            HttpContext http, OperationsCaseAccess access, OperationsDbContext db,
            Guid? companyId, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.case.create") &&
                !OperationsCaseAccess.HasScope(http.User, "operations.aperture.validation.assign"))
                return Results.Forbid();
            var ownCompany = await access.ResolveCompanyIdAsync(http.User, ct);
            var company = OperationsCaseAccess.IsSystemAdministrator(http.User) && companyId.HasValue
                ? companyId : ownCompany;
            if (company is null || company == Guid.Empty) return Results.Forbid();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                ownCompany != company) return Results.Forbid();

            var teams = await db.Teams.AsNoTracking()
                .Where(t => t.CompanyId == company && t.IsActive && t.DepartmentCode == "APERTURAS")
                .Select(t => new {
                    t.Id, t.Name, t.DepartmentCode,
                    AvailableReviewers = t.Members
                        .Where(m => m.IsAvailable && m.CapacityPoints > 0)
                        .Select(m => m.UserId).Distinct().Count()
                })
                .OrderBy(t => t.Name).ToArrayAsync(ct);
            return Results.Ok(teams.Select(t => new {
                t.Id, t.Name, t.DepartmentCode, t.AvailableReviewers,
                CanAssign = t.AvailableReviewers >= 2
            }));
        });

        api.MapPost("/cases/{caseId:guid}/aperture-validations/assign", async (
            Guid caseId, AssignReviewersRequest input, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, OperationsAuthUserVerifier auth, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null) return Results.Forbid();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var operation = await db.Cases.SingleOrDefaultAsync(c => c.Id == caseId, ct);
            if (operation is null) return Results.NotFound();
            // Manual-case creators may choose an Aperturas team for their own new
            // case; broader assignment privileges still require both management scopes.
            var manager = OperationsCaseAccess.IsSystemAdministrator(http.User) ||
                (OperationsCaseAccess.HasScope(http.User, "operations.aperture.validation.assign") &&
                 OperationsCaseAccess.HasScope(http.User, "operations.team.manage"));
            var creator = operation.ApertureType == "MANUAL" && operation.CreatedBy == actor.Value &&
                operation.Status == "DRAFT" &&
                OperationsCaseAccess.HasScope(http.User, "operations.case.create") &&
                input.TeamId.HasValue;
            if (!manager && !creator) return Results.Forbid();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                await access.ResolveCompanyIdAsync(http.User, ct) != operation.CompanyId) return Results.Forbid();
            var key = "apertures-pool:" + operation.CompanyId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
            var stage = await db.CaseStages.SingleOrDefaultAsync(s =>
                s.CaseId == caseId && s.DepartmentCode == "APERTURAS", ct);
            if (stage is null) return Results.NotFound();
            if (operation.Status is "CANCELLED" or "COMPLETED" or "APERTURA_VALIDADA" ||
                stage.Status is "COMPLETED" or "SKIPPED" or "CHANGES_REQUIRED")
                return Results.Conflict(new { code = "APERTURE_NOT_ASSIGNABLE" });
            if (operation.ApertureType == "MANUAL" && (operation.Status == "DRAFT" || operation.OpeningDataJson != "{}"))
            {
                var missing = OperationsOpeningDraftValidator.MissingFields(
                    operation.ModalityCode, operation.OpeningDataJson);
                if (missing.Length > 0)
                    return Results.Conflict(new {
                        code = "APERTURE_MISSING_REQUIRED_FIELDS", missingFields = missing
                    });
            }
            var existing = await db.ApertureValidations.AsNoTracking()
                .Where(x => x.CaseStageId == stage.Id).OrderBy(x => x.Slot)
                .Select(x => new { x.Id, x.UserId, x.Slot, x.Status }).ToArrayAsync(ct);
            if (existing.Length == 2)
                return Results.Ok(new { stageId = stage.Id, assigned = existing, duplicate = true });
            if (existing.Length != 0)
                return Results.Conflict(new { code = "INCOMPLETE_VALIDATION_PAIR" });

            var rules = await db.AssignmentRules.AsNoTracking().Where(r =>
                r.IsActive && r.Team.IsActive && r.Team.CompanyId == operation.CompanyId &&
                r.DepartmentCode == "APERTURAS" &&
                (r.ModalityCode == null || r.ModalityCode == operation.ModalityCode) &&
                (r.OriginCountryCode == null || r.OriginCountryCode == operation.OriginCountryCode) &&
                (r.DestinationCountryCode == null ||
                    r.DestinationCountryCode == operation.DestinationCountryCode))
                .Select(r => new { r.TeamId, r.Priority }).ToArrayAsync(ct);
            var teamIds = rules.Select(r => r.TeamId).Distinct().ToArray();
            var teamsQuery = db.Teams.AsNoTracking().Include(t => t.Members).Where(t =>
                t.IsActive && t.CompanyId == operation.CompanyId && t.DepartmentCode == "APERTURAS");
            if (input.TeamId.HasValue)
                teamsQuery = teamsQuery.Where(t => t.Id == input.TeamId.Value);
            else if (teamIds.Length > 0)
                teamsQuery = teamsQuery.Where(t => teamIds.Contains(t.Id));
            var teams = await teamsQuery.ToArrayAsync(ct);
            if (input.TeamId.HasValue && teams.Length == 0)
                return Results.BadRequest(new { code = "APERTURE_TEAM_NOT_AVAILABLE",
                    message = "El equipo seleccionado no existe, no pertenece a Aperturas o está inactivo." });
            var workloads = await db.ApertureValidations.AsNoTracking()
                .Where(v => v.Status == "PENDING" && v.CaseStage.Case.CompanyId == operation.CompanyId)
                .GroupBy(v => v.UserId).Select(g => new {
                    UserId = g.Key, Points = g.Sum(v => v.CaseStage.Case.ComplexityPoints / 2m)
                }).ToDictionaryAsync(x => x.UserId, x => x.Points, ct);
            var candidates = teams.SelectMany(team => team.Members
                .Where(member => member.IsAvailable && member.CapacityPoints > 0)
                .Select(member => new AperturePoolMember(team.Id, member.UserId,
                    member.CapacityPoints, workloads.GetValueOrDefault(member.UserId),
                    rules.Where(r => r.TeamId == team.Id).Select(r => r.Priority)
                        .DefaultIfEmpty(0).Max()))).ToList();
            AperturePoolPair? pair;
            try
            {
                while ((pair = OperationsDualPoolScoring.Select(candidates,
                    operation.ComplexityPoints)) is not null)
                {
                    var validFirst = await auth.IsEligibleAsync(pair.FirstUserId,
                        http.Request.Headers.Authorization.ToString(), ct, requireReview: true);
                    var validSecond = await auth.IsEligibleAsync(pair.SecondUserId,
                        http.Request.Headers.Authorization.ToString(), ct, requireReview: true);
                    if (validFirst && validSecond) break;
                    candidates.RemoveAll(x => (x.UserId == pair.FirstUserId && !validFirst) ||
                        (x.UserId == pair.SecondUserId && !validSecond));
                }
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (HttpRequestException) {
                return Results.Problem(statusCode: 503, title: "Auth unavailable, validation not assigned");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) {
                return Results.Problem(statusCode: 503, title: "Auth timed out, validation not assigned");
            }
            if (pair is null)
                return Results.Conflict(new {
                    code = "POOL_REQUIRES_TWO_ELIGIBLE_DISTINCT_USERS",
                    message = "El equipo no tiene dos validadores distintos, disponibles y autorizados en Auth. Configure su equipo o seleccione otro; el IDTRA existente se conserva."
                });

            var assignments = new[] {
                new OperationsApertureValidation { CaseStageId = stage.Id,
                    TeamId = pair.TeamId, UserId = pair.FirstUserId, Slot = 1,
                    AssignedBy = actor.Value },
                new OperationsApertureValidation { CaseStageId = stage.Id,
                    TeamId = pair.TeamId, UserId = pair.SecondUserId, Slot = 2,
                    AssignedBy = actor.Value }
            };
            db.ApertureValidations.AddRange(assignments);
            stage.TeamId = pair.TeamId;
            stage.Status = "PENDING_VALIDATION";
            stage.Version++;
            operation.Status = "PENDING_VALIDATION";
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, caseId, stage.Id, actor.Value,
                "operations.aperture.validation.assigned", null,
                new { teamId = pair.TeamId, slots = assignments.Select(a =>
                    new { a.Slot, a.UserId }).ToArray() });
            // Sanitized case event for the existing Operations SignalR inbox/relay.
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.aperture.validation.assigned.v1",
                JsonSerializer.Serialize(new { caseId, stageId = stage.Id, operation.CompanyId,
                    version = operation.Version })));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { stageId = stage.Id, teamId = pair.TeamId,
                assigned = assignments.Select(a => new { a.Id, a.Slot, a.UserId, a.Status }), duplicate = false });
        });

        api.MapPost("/cases/{caseId:guid}/aperture-validations/{validationId:guid}/decision", async (
            Guid caseId, Guid validationId, ReviewDecisionRequest input, HttpContext http,
            OperationsDbContext db, AssignStageHandler originAssigner,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null ||
                !OperationsCaseAccess.HasScope(http.User, "operations.case.read") ||
                !OperationsCaseAccess.HasScope(http.User, "operations.aperture.validation.review"))
                return Results.Forbid();
            var decision = input.Decision?.Trim().ToUpperInvariant();
            if (decision is not ("APPROVED" or "REJECTED") ||
                input.Comment?.Length > 2000 ||
                (decision == "REJECTED" && string.IsNullOrWhiteSpace(input.Comment)))
                return Results.BadRequest(new { code = "VALIDATION_DECISION_INVALID" });
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var key = "apertures-decision:" + caseId.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));", ct);
            var validation = await db.ApertureValidations.SingleOrDefaultAsync(v =>
                v.Id == validationId && v.CaseStage.CaseId == caseId, ct);
            if (validation is null) return Results.NotFound();
            if (validation.UserId != actor.Value) return Results.Forbid();
            if (validation.Status == decision)
                return Results.Ok(new { validation.Id, validation.Status, duplicate = true });
            if (validation.Status != "PENDING")
                return Results.Conflict(new { code = "VALIDATION_ALREADY_RESOLVED" });
            var stage = await db.CaseStages.SingleAsync(s => s.Id == validation.CaseStageId, ct);
            var operation = await db.Cases.SingleAsync(c => c.Id == caseId, ct);
            if (stage.Status != "PENDING_VALIDATION" || operation.Status != "PENDING_VALIDATION")
                return Results.Conflict(new { code = "APERTURE_REVIEW_CLOSED" });
            var count = await db.ApertureValidations.CountAsync(v =>
                v.CaseStageId == stage.Id, ct);
            if (count != 2) return Results.Conflict(new { code = "TWO_VALIDATORS_REQUIRED" });
            validation.Status = decision!;
            validation.Comment = input.Comment?.Trim();
            validation.ReviewedAtUtc = DateTimeOffset.UtcNow;
            var otherApproved = await db.ApertureValidations.AnyAsync(v =>
                v.CaseStageId == stage.Id && v.Id != validation.Id && v.Status == "APPROVED", ct);
            var bothApproved = decision == "APPROVED" && otherApproved;
            Guid? originStageId = null;
            if (decision == "REJECTED")
            {
                stage.Status = "CHANGES_REQUIRED";
                operation.Status = "APERTURA_REQUIERE_CAMBIOS";
            }
            else if (bothApproved)
            {
                stage.Status = "COMPLETED";
                stage.CompletedAtUtc = DateTimeOffset.UtcNow;
                operation.Status = "APERTURA_VALIDADA";
                // Origin starts only after BOTH reviewers approve the same opening.
                var existingOrigin = await db.CaseStages.SingleOrDefaultAsync(s =>
                    s.CaseId == caseId && s.DepartmentCode == "COORDINACION_ORIGEN", ct);
                if (existingOrigin is null)
                {
                    existingOrigin = new OperationsCaseStage {
                        CaseId = caseId, DepartmentCode = "COORDINACION_ORIGEN", Status = "PENDING"
                    };
                    db.CaseStages.Add(existingOrigin);
                }
                originStageId = existingOrigin.Id;
                db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                    "operations.case.validated.v1",
                    JsonSerializer.Serialize(new { caseId, stageId = stage.Id,
                        originStageId, actorId = actor.Value, operation.CompanyId })));
            }
            stage.Version++;
            operation.Version++;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OperationsAuditWriter.Case(db, caseId, stage.Id, actor.Value,
                "operations.aperture.validation.reviewed",
                new { validationId, status = "PENDING" },
                new { validationId, decision, slot = validation.Slot, bothApproved });
            db.OutboxMessages.Add(OperationsOutboxFactory.Create(
                "operations.aperture.validation.reviewed.v1",
                JsonSerializer.Serialize(new { caseId, stageId = stage.Id, operation.CompanyId,
                    version = operation.Version, status = operation.Status })));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            StageAssignedResult? originAssignment = null;
            if (bothApproved && originStageId.HasValue)
            {
                try
                {
                    // Atomic weighted-pool assignment reuses the existing handler.
                    originAssignment = await originAssigner.HandleAsync(
                        new AssignStageCommand(caseId, originStageId.Value, actor.Value), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The approval is committed; a worker retries the unassigned
                    // stage after transient failures instead of claiming success.
                    loggerFactory.CreateLogger("Operations.Apertures.Validation")
                        .LogError(ex, "Origin assignment pending for validated case {CaseId}", caseId);
                }
            }
            return Results.Ok(new { validation.Id, validation.Status,
                stageStatus = stage.Status, operationStatus = operation.Status,
                bothApproved, originStageId, originAssigned = originAssignment is not null,
                originAssigneeId = originAssignment?.UserId, duplicate = false });
        });

        api.MapPatch("/teams/{teamId:guid}/active", async (
            Guid teamId, SetTeamActiveRequest input, HttpContext http,
            OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.team.manage"))
                return Results.Forbid();
            var team = await db.Teams.SingleOrDefaultAsync(t => t.Id == teamId, ct);
            if (team is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                await access.ResolveCompanyIdAsync(http.User, ct) != team.CompanyId) return Results.Forbid();
            if (team.IsActive == input.IsActive) return Results.Ok(new { team.Id, team.IsActive, duplicate = true });
            OperationsAuditWriter.Team(db, teamId, actor.Value, "operations.team.active.changed",
                new { team.IsActive }, new { input.IsActive });
            team.IsActive = input.IsActive;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { team.Id, team.IsActive });
        });

        api.MapDelete("/teams/{teamId:guid}/members/{userId:guid}", async (
            Guid teamId, Guid userId, HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            var actor = OperationsCaseAccess.UserId(http.User);
            if (actor is null || !OperationsCaseAccess.HasScope(http.User, "operations.team.manage"))
                return Results.Forbid();
            var team = await db.Teams.SingleOrDefaultAsync(t => t.Id == teamId, ct);
            if (team is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                await access.ResolveCompanyIdAsync(http.User, ct) != team.CompanyId) return Results.Forbid();
            var member = await db.TeamMembers.SingleOrDefaultAsync(m =>
                m.TeamId == teamId && m.UserId == userId, ct);
            if (member is null) return Results.NoContent();
            var pending = await db.ApertureValidations.AnyAsync(v =>
                v.TeamId == teamId && v.UserId == userId && v.Status == "PENDING", ct);
            if (pending) return Results.Conflict(new { code = "MEMBER_HAS_PENDING_VALIDATIONS" });
            OperationsAuditWriter.Team(db, teamId, actor.Value, "operations.team.member.removed",
                new { member.UserId, member.CapacityPoints, member.IsSupervisor, member.IsAvailable },
                null);
            db.TeamMembers.Remove(member);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/teams/{teamId:guid}/audit", async (
            Guid teamId, HttpContext http, OperationsCaseAccess access, OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.audit.read") ||
                !OperationsCaseAccess.HasScope(http.User, "operations.team.manage"))
                return Results.Forbid();
            var team = await db.Teams.AsNoTracking().SingleOrDefaultAsync(t => t.Id == teamId, ct);
            if (team is null) return Results.NotFound();
            if (!OperationsCaseAccess.IsSystemAdministrator(http.User) &&
                await access.ResolveCompanyIdAsync(http.User, ct) != team.CompanyId) return Results.Forbid();
            return Results.Ok(await db.TeamAuditEntries.AsNoTracking()
                .Where(a => a.TeamId == teamId).OrderByDescending(a => a.OccurredAtUtc)
                .Take(100).Select(a => new { a.EventId, a.ActionCode, a.ActorUserId,
                    a.BeforeJson, a.AfterJson, a.OccurredAtUtc }).ToArrayAsync(ct));
        });

        api.MapGet("/cases/{caseId:guid}/audit", async (
            Guid caseId, HttpContext http, OperationsCaseAccess access,
            OperationsDbContext db, CancellationToken ct) =>
        {
            if (!OperationsCaseAccess.HasScope(http.User, "operations.audit.read") ||
                !await access.CanReadAsync(caseId, http.User, ct)) return Results.Forbid();
            return Results.Ok(await db.CaseAuditEntries.AsNoTracking()
                .Where(a => a.CaseId == caseId).OrderByDescending(a => a.OccurredAtUtc)
                .Take(100).Select(a => new { a.EventId, a.ActionCode, a.ActorUserId,
                    a.BeforeJson, a.AfterJson, a.OccurredAtUtc }).ToArrayAsync(ct));
        });

        return app;
    }
    public sealed record SetTeamActiveRequest(bool IsActive);
}
