using System.Net;
using System.Text.Json.Nodes;
using Dhole.Operations.Api.Integrations;
using Microsoft.Extensions.Configuration;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsCrmDivisionValidationTests
{
    private static readonly Guid Division = Guid.NewGuid();
    private static readonly Guid Contact = Guid.NewGuid();
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new {
                divisions = new[] { new { id = Division, name = "Exportaciones" } },
                contacts = new[] { new { id = Contact } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private static OperationsCrmAccountResolver Resolver(HttpClient client) => new(new Factory(client),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Crm:ApiBaseUrl"] = "http://crm-api:8080" }).Build());

    [TestMethod]
    public async Task Canonicalizes_division_name_and_accepts_only_crm_contacts()
    {
        using var client = new HttpClient(new Handler());
        var data = new JsonObject { ["crmDivisionId"] = Division.ToString(), ["crmDivisionName"] = "Forged",
            ["contactIds"] = new JsonArray(JsonValue.Create(Contact.ToString())) };
        var result = await Resolver(client).ValidateDivisionAsync(Guid.NewGuid(), data, "Bearer test", null, CancellationToken.None);
        Assert.IsNull(result.ErrorCode);
        Assert.AreEqual("Exportaciones", data["crmDivisionName"]!.GetValue<string>());
        data["contactIds"] = new JsonArray(JsonValue.Create(Guid.NewGuid().ToString()));
        result = await Resolver(client).ValidateDivisionAsync(Guid.NewGuid(), data, "Bearer test", null, CancellationToken.None);
        Assert.AreEqual("CRM_CONTACT_NOT_IN_DIVISION", result.ErrorCode);
    }
    [TestMethod]
    public async Task Legacy_opening_without_division_does_not_require_crm_directory()
    {
        using var client = new HttpClient(new Handler());
        var result = await Resolver(client).ValidateDivisionAsync(Guid.NewGuid(), new JsonObject(), "", null, CancellationToken.None);
        Assert.IsNull(result.ErrorCode);
    }
}
