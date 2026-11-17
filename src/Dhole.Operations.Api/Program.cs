using System.Text;
using System.Text.Json;
using Dhole.Operations.Api.Endpoints;
using Dhole.Operations.Api.Grpc;
using Dhole.Operations.Api.Hubs;
using Dhole.Operations.Api.Realtime;
using Dhole.Operations.Api.Security;
using Dhole.Operations.Application.DependencyInjection;
using Dhole.Operations.Infrastructure.DependencyInjection;
using Dhole.Operations.Persistence;
using Dhole.Operations.Persistence.DependencyInjection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddSignalR();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("OperationsRealtimeRead", policy => policy.RequireAssertion(context =>
        OperationsCaseAccess.HasScope(context.User, "operations.realtime.read")));
});
builder.Services.AddOperationsApplication();
builder.Services.AddOperationsPersistence(builder.Configuration);
builder.Services.AddOperationsInfrastructure(builder.Configuration);
builder.Services.AddScoped<OperationsCaseAccess>();
builder.Services.AddHostedService<OperationsRealtimeRelay>();

var secret = builder.Configuration["Auth:Jwt:SecretKey"];
if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
    throw new InvalidOperationException("Auth:Jwt:SecretKey must be supplied through secure environment configuration (32+ bytes).");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new()
    {
        ValidateIssuer = true, ValidIssuer = builder.Configuration["Auth:Jwt:Issuer"] ?? "DholeAuthService",
        ValidateAudience = true, ValidAudience = builder.Configuration["Auth:Jwt:Audience"] ?? "DholeEcosystem",
        ValidateLifetime = true, ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret))
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/operations"))
                context.Token = token;
            return Task.CompletedTask;
        }
    };
});
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("DholeWebCors",
    p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseCors("DholeWebCors");
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { service = "DholeOperationsService", status = "Healthy", version = typeof(Program).Assembly.GetName().Version?.ToString(3), timeUtc = DateTimeOffset.UtcNow }))
   .AllowAnonymous();
app.MapGet("/ready", async (OperationsDbContext db, IConnectionMultiplexer redis, CancellationToken ct) =>
{
    if (!await db.Database.CanConnectAsync(ct) || !redis.IsConnected)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    return Results.Ok(new { status = "Ready", version = typeof(Program).Assembly.GetName().Version?.ToString(3) });
}).AllowAnonymous();

app.MapGet("/api/operations/realtime/events", async (
    DateTimeOffset? since, int? take, HttpContext http, OperationsCaseAccess access,
    OperationsDbContext db, CancellationToken ct) =>
{
    var limit = Math.Clamp(take ?? 100, 1, 500);
    // Never expose raw PayloadJson: it can contain cross-service or commercially restricted data.
    var query = db.InboxMessages.AsNoTracking().Where(e => e.ConsumerService == "operations-realtime");
    if (since.HasValue) query = query.Where(e => e.ProcessedAtUtc > since.Value.UtcDateTime);
    var receipts = await query.OrderBy(e => e.ProcessedAtUtc).ThenBy(e => e.EventId)
        .Take(limit).Select(e => new { e.EventId, e.EventName, e.PayloadJson, e.ProcessedAtUtc, e.CreatedAtUtc, PayloadJson = EF.Property<string>(e, "PayloadJson") })
        .ToListAsync(ct);
    var parsed = new List<(Guid EventId, string EventName, Guid CaseId, DateTimeOffset At)>();
    foreach (var receipt in receipts)
    {
        try
        {
            using var json = JsonDocument.Parse(receipt.PayloadJson);
            if (!json.RootElement.TryGetProperty("caseId", out var value) ||
                !Guid.TryParse(value.GetString(), out var caseId)) continue;
            parsed.Add((receipt.EventId, receipt.EventName, caseId, receipt.ProcessedAtUtc ?? receipt.CreatedAtUtc));
        }
        catch (JsonException) { /* Malformed external events never become public replay data. */ }
    }
    var allowed = (await access.AccessibleCaseIdsAsync(
        parsed.Select(x => x.CaseId).Distinct().ToArray(), http.User, ct)).ToHashSet();
    return Results.Ok(parsed.Where(x => allowed.Contains(x.CaseId)).Select(x =>
        new { x.EventId, x.EventName, x.CaseId, ProcessedAtUtc = x.At }));
}).RequireAuthorization("OperationsRealtimeRead");
app.MapOperationsEndpoints();
app.MapHub<OperationsHub>("/hubs/operations");
app.MapGrpcService<OperationsHealthGrpcService>();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
    await db.Database.MigrateAsync();
}
// Dhole API Manager: authenticated runtime inventory, not a public proxy.
app.MapGet("/api/internal/third-party-route-catalog", (HttpContext http) =>
{
    var allowed = http.User.Identity?.IsAuthenticated == true &&
        http.User.Claims.Where(c => c.Type is "scope" or "scp" or "scopes")
            .SelectMany(c => c.Value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Contains("integrations.thirdparty.manage", StringComparer.OrdinalIgnoreCase);
    if (!allowed) return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
    var routes = new List<object>();
    foreach (var source in ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources)
        foreach (var endpoint in source.Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || endpoint.RoutePattern.RawText is not { } path) continue;
            foreach (var method in methods)
                if (method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE")
                    routes.Add(new { method, path = "/" + path.TrimStart('/') });
        }
    return (IResult)Results.Ok(routes);
}).RequireAuthorization().ExcludeFromDescription();

app.Run();

public partial class Program {}
