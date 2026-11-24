using System.Text.Json;

namespace Dhole.Operations.Api.Integrations;

/// <summary>Resolve only responsible users of already authorized cases. A failed Auth
/// lookup must not prevent reading a case or disclose unrelated user information.</summary>
public sealed class OperationsAssignedUserLabels(IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<Dictionary<Guid, string>> ReadAsync(IEnumerable<Guid> authorizedIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        var address = configuration["Auth:OperationsPool:BaseAddress"];
        var key = configuration["Auth:OperationsPool:InternalServiceKey"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(key)) return result;
        foreach (var batch in authorizedIds.Where(x => x != Guid.Empty).Distinct().Chunk(8))
        {
            var names = await Task.WhenAll(batch.Select(async userId =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(uri.ToString().TrimEnd('/') + "/"),
                        $"api/internal/auth/operations-users/{userId:D}/eligibility"));
                    req.Headers.TryAddWithoutValidation("X-Dhole-Service-Key", key);
                    using var response = await clients.CreateClient("auth-operations-users")
                        .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode) return (userId, (string?)null);
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    return (userId, doc.RootElement.TryGetProperty("displayName", out var name) &&
                        name.ValueKind == JsonValueKind.String ? name.GetString() : null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (HttpRequestException) { return (userId, (string?)null); }
                catch (OperationCanceledException) { return (userId, (string?)null); }
                catch (JsonException) { return (userId, (string?)null); }
            }));
            foreach (var (id, name) in names)
                if (!string.IsNullOrWhiteSpace(name)) result[id] = name;
        }
        return result;
    }
}
