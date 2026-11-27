using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Dhole.Operations.Api.Integrations;

/// <summary>CRM owns account/executive identity. Never trust a creator-provided sales executive.</summary>
public sealed class OperationsCrmAccountResolver(IHttpClientFactory factory, IConfiguration configuration)
{
    public sealed record Resolution(Guid? ExecutiveUserId, string? ErrorCode, int StatusCode);

    public async Task<Resolution> ResolveAsync(Guid accountId, string authorization, CancellationToken ct)
    {
        var configured = configuration["Crm:ApiBaseUrl"];
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUrl.UserInfo))
            return new(null, "CRM_NOT_CONFIGURED", 503);
        if (!AuthenticationHeaderValue.TryParse(authorization, out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            return new(null, "AUTHORIZATION_REQUIRED", 401);

        var url = new Uri(new Uri(baseUrl.ToString().TrimEnd('/') + "/"),
            $"api/crm/operations/accounts/{accountId:D}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = bearer;
        try
        {
            using var response = await factory.CreateClient("crm-operations-contacts")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(null, "CRM_ACCESS_DENIED", 403);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(null, "CRM_ACCESS_DENIED", 401);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, "CRM_ACCOUNT_NOT_FOUND", 404);
            if (!response.IsSuccessStatusCode) return new(null, "CRM_UNAVAILABLE", 503);

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, "CRM_INVALID_CONTRACT", 503);
            Guid executiveId = Guid.Empty;
            foreach (var property in root.EnumerateObject())
                if (string.Equals(property.Name, "executiveUserId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    Guid.TryParse(property.Value.GetString(), out executiveId);
            // No fallback to creator or fictitious staff, even for an institutional account.
            if (executiveId == Guid.Empty) return new(null, "CRM_EXECUTIVE_UNASSIGNED", 409);
            return new(executiveId, null, 200);
        }
        catch (HttpRequestException) { return new(null, "CRM_UNAVAILABLE", 503); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, "CRM_TIMEOUT", 503);
        }
        catch (JsonException) { return new(null, "CRM_INVALID_CONTRACT", 503); }
    }
}
