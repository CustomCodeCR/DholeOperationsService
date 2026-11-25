using System.Net;
using System.Text.Json;
using Dhole.Operations.Application.Cases;
using Microsoft.Extensions.Configuration;

namespace Dhole.Operations.Infrastructure;

public sealed class OperationsAssignmentEligibility(HttpClient client, IConfiguration config) : IOperationsAssignmentEligibility
{
    public async Task<bool> IsEligibleAsync(Guid userId, CancellationToken ct)
    {
        var key = config["Auth:OperationsPool:InternalServiceKey"];
        if (!Uri.TryCreate(config["Auth:OperationsPool:BaseAddress"], UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.UserInfo) ||
            string.IsNullOrWhiteSpace(key)) throw new HttpRequestException("Internal Auth eligibility is not configured");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(address.ToString().TrimEnd('/') + "/"),
            $"api/internal/auth/operations-users/{userId:D}/eligibility"));
        request.Headers.TryAddWithoutValidation("X-Dhole-Service-Key", key);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Internal Auth eligibility unavailable");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("eligibleForOperations", out var eligible) && eligible.ValueKind == JsonValueKind.True;
    }
}
