using System.Text;
using Dhole.Operations.Api.Grpc;
using Dhole.Operations.Api.Hubs;
using Dhole.Operations.Api.Realtime;
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
builder.Services.AddAuthorization();
builder.Services.AddOperationsPersistence(builder.Configuration);
builder.Services.AddOperationsInfrastructure(builder.Configuration);
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
app.MapGet("/health", () => Results.Ok(new { service = "DholeOperationsService", status = "Healthy", timeUtc = DateTimeOffset.UtcNow }))
   .AllowAnonymous();
app.MapGet("/ready", async (OperationsDbContext db, IConnectionMultiplexer redis, CancellationToken ct) =>
{
    if (!await db.Database.CanConnectAsync(ct) || !redis.IsConnected)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    return Results.Ok(new { status = "Ready" });
}).AllowAnonymous();

app.MapGet("/api/operations/realtime/events", async (
    DateTimeOffset? since, int? take, OperationsDbContext db, CancellationToken ct) =>
{
    var limit = Math.Clamp(take ?? 100, 1, 500);
    var query = db.InboxReceipts.AsNoTracking().Where(e => e.ConsumerName == "operations-realtime");
    if (since.HasValue) query = query.Where(e => e.ProcessedAtUtc > since.Value);
    var events = await query.OrderBy(e => e.ProcessedAtUtc).Take(limit)
        .Select(e => new { e.EventId, e.EventName, e.PayloadJson, e.ProcessedAtUtc })
        .ToListAsync(ct);
    return Results.Ok(events);
}).RequireAuthorization();
app.MapHub<OperationsHub>("/hubs/operations");
app.MapGrpcService<OperationsHealthGrpcService>();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OperationsDbContext>();
    await db.Database.MigrateAsync();
}
app.Run();

public partial class Program {}
