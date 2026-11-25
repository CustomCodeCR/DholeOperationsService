using System.Net;
using System.Text;
using System.Text.Json;
using Dhole.Operations.Api.Integrations;
using Microsoft.Extensions.Configuration;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsOpeningCatalogValidationTests
{
    private const string OriginId = "11111111-1111-4111-8111-111111111111";
    private const string EntryId = "22222222-2222-4222-8222-222222222222";
    private const string DestinationId = "33333333-3333-4333-8333-333333333333";

    private static string Opening() => JsonSerializer.Serialize(new
    {
        transportType = "air", shipmentType = "AIR",
        pol = "Origin", poe = "Entry", pod = "Destination",
        polCatalogId = OriginId, poeCatalogId = EntryId, podCatalogId = DestinationId,
        polCatalogCode = "ORIGIN", poeCatalogCode = "ENTRY", podCatalogCode = "DESTINATION",
        polTerminalType = "APT", poeTerminalType = "APT", podTerminalType = "SD"
    });

    private sealed class CatalogHandler(string originType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = request.RequestUri!.Query;
            var origin = query.Contains("catalogGroupSlug=pol&", StringComparison.Ordinal);
            var entry = query.Contains("catalogGroupSlug=poe&", StringComparison.Ordinal);
            var id = origin ? OriginId : entry ? EntryId : DestinationId;
            var label = origin ? "Origin" : entry ? "Entry" : "Destination";
            var terminal = origin ? originType : entry ? "APT" : "SD";
            var body = JsonSerializer.Serialize(new { data = new[] { new
            {
                id, value = label, label, code = label.ToUpperInvariant(),
                isActive = true, metadataJson = JsonSerializer.Serialize(new { terminalType = terminal })
            } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Config:ApiBaseUrl"] = "http://config-api:8080" }).Build();

    [TestMethod]
    public async Task AcceptsActiveAirportsEvenWithLowerCaseTransport()
    {
        using var client = new HttpClient(new CatalogHandler("APT"));
        var validator = new OperationsOpeningCatalogValidator(new Factory(client), Config());
        var result = await validator.ValidateAsync(Opening(), "Bearer test", CancellationToken.None);
        Assert.IsNull(result.ErrorCode);
        Assert.AreEqual(200, result.StatusCode);
    }

    [TestMethod]
    public async Task RejectsRealSeaPortDespiteClientClaimingAirport()
    {
        using var client = new HttpClient(new CatalogHandler("CY"));
        var validator = new OperationsOpeningCatalogValidator(new Factory(client), Config());
        var result = await validator.ValidateAsync(Opening(), "Bearer test", CancellationToken.None);
        Assert.AreEqual("APERTURE_CATALOG_INVALID", result.ErrorCode);
        CollectionAssert.Contains(result.Fields, "pol");
    }
}
