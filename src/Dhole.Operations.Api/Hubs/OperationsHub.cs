using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Dhole.Operations.Api.Hubs;

[Authorize]
public sealed class OperationsHub : Hub
{
    // The client reconciles after reconnection via GET /api/operations/realtime/events.
}
