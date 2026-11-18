using System.Net;
using System.Net.Http.Headers;

namespace Dhole.Operations.Api.Integrations;

/// <summary>Fail-closed Auth lookups: only internal, active, unlocked users can be allocated.</summary>
public sealed class OperationsAuthUserVerifier(IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<bool> IsEligibleAsync(Guid userId, string authorization, CancellationToken ct)
    {
        if (userId == Guid.Empty) return false;
        var configured = configuration["Auth:ApiBaseUrl"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != "http" && baseUri.Scheme != "https") ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
            throw new HttpRequestException("Auth service address is not configured");
        if (!AuthenticationHeaderValue.TryParse(authorization, out var token) ||
            !string.Equals(token.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Bearer token required for Auth eligibility");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(
            new Uri(baseUri.ToString().TrimEnd('/') + "/"),
            $"api/auth/users/{userId:D}/operations-eligibility"));
        request.Headers.Authorization = token;
        using var response = await clients.CreateClient("auth-operations-users")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Auth does not permit eligibility lookup");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Auth eligibility could not be verified");
        return true;
    }
}
