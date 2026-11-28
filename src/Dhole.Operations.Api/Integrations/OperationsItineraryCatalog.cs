using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.Api.Integrations;

public sealed class OperationsItineraryCatalog(IHttpClientFactory factory, IConfiguration configuration)
{
    public async Task<IReadOnlyDictionary<Guid, OperationsItineraryPolicy.Location>?> ResolveAsync(
        Guid[] ids, string authorization, CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration["Config:ApiBaseUrl"], UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) ||
            !AuthenticationHeaderValue.TryParse(authorization, out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(bearer.Parameter)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/"),
            "api/config/operations/route-locations/resolve"));
        request.Headers.Authorization = bearer;
        request.Content = JsonContent.Create(new { ids });
        try
        {
            using var response = await factory.CreateClient("config-operations-opening").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var rows = await response.Content.ReadFromJsonAsync<OperationsItineraryPolicy.Location[]>(cancellationToken: ct);
            if (rows is null || rows.Length != ids.Length || rows.Any(r => r is null ||
                !ids.Contains(r.Id) || !OperationsItineraryPolicy.Country(r.CountryCode) ||
                string.IsNullOrWhiteSpace(r.Label) || r.Label.Length > 250 ||
                r.TerminalType is not ("CY" or "APT" or "SD")) || rows.Select(r => r.Id).Distinct().Count() != ids.Length) return null;
            return rows.ToDictionary(r => r.Id);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
