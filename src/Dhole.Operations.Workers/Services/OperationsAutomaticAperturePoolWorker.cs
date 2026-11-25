using System.Net;
using System.Text.Json;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.Cases;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Operations.Workers.Services;

/// <summary>Reconciles every pending opening, including previously imported IDTRAs.
/// Failed or ineligible cases stay pending and cannot starve later cases.</summary>
public sealed class OperationsAutomaticAperturePoolWorker(IServiceScopeFactory scopes,
    IHttpClientFactory clients, IConfiguration configuration,
    ILogger<OperationsAutomaticAperturePoolWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Guid[] ids;
                using (var scan = scopes.CreateScope())
                {
                    var db = scan.ServiceProvider.GetRequiredService<OperationsDbContext>();
                    ids = await db.CaseStages.AsNoTracking()
                        .Where(s => s.DepartmentCode == "APERTURAS" &&
                            (s.Status == "PENDING" || s.Status == "PENDING_VALIDATION") &&
                            s.Case.Status == "PENDING_VALIDATION" && s.Case.ApertureType == "AUTOMATIC" &&
                            (!db.ApertureValidations.Any(v => v.CaseStageId == s.Id) ||
                             (db.ApertureValidations.Count(v => v.CaseStageId == s.Id) == 1 &&
                              db.ApertureValidations.Any(v => v.CaseStageId == s.Id && v.Slot == 1 && v.Status == "APPROVED")) ||
                             db.ApertureValidations.Count(v => v.CaseStageId == s.Id && v.Status == "PENDING" &&
                                 v.ReviewedAtUtc == null && v.Comment == null) == 2))
                        .OrderBy(s => s.Case.CreatedAtUtc).ThenBy(s => s.Id)
                        .Select(s => s.CaseId).ToArrayAsync(stoppingToken);
                }
                var cache = new Dictionary<Guid, bool>();
                var assigned = 0;
                async Task<bool> Eligible(Guid userId, CancellationToken ct)
                {
                    if (cache.TryGetValue(userId, out var previous)) return previous;
                    var result = await VerifyAsync(userId, ct);
                    cache[userId] = result;
                    return result;
                }
                foreach (var id in ids)
                {
                    using var scope = scopes.CreateScope();
                    try
                    {
                        if (await OperationsAutomaticAperturePool.TryAssignAsync(
                            scope.ServiceProvider.GetRequiredService<OperationsDbContext>(), id, Eligible, stoppingToken))
                            assigned++;
                    }
                    catch (HttpRequestException) { throw; } // retry whole cycle on Auth failure, fail closed
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogError(ex, "Automatic Aperturas assignment failed for {CaseId}", id); }
                }
                if (ids.Length > 0)
                    logger.LogInformation("Automatic Aperturas pool: pending={Pending}, assigned={Assigned}, remaining={Remaining}", ids.Length, assigned, ids.Length - assigned);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Automatic Aperturas pool cycle failed; will retry"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task<bool> VerifyAsync(Guid userId, CancellationToken ct)
    {
        var address = configuration["Auth:OperationsPool:BaseAddress"];
        var key = configuration["Auth:OperationsPool:InternalServiceKey"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(key))
            throw new HttpRequestException("Internal Auth eligibility is not configured");
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(uri.ToString().TrimEnd('/') + "/"),
            $"api/internal/auth/operations-users/{userId:D}/eligibility"));
        req.Headers.TryAddWithoutValidation("X-Dhole-Service-Key", key);
        using var response = await clients.CreateClient("auth-operations-pool")
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Internal Auth eligibility unavailable");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.TryGetProperty("eligibleForOperations", out var eligible) &&
            eligible.ValueKind == JsonValueKind.True &&
            document.RootElement.TryGetProperty("canReviewAperturas", out var review) && review.ValueKind == JsonValueKind.True;
    }
}
