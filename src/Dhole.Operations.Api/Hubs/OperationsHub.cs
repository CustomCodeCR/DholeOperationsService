using Dhole.Operations.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Dhole.Operations.Api.Hubs;

[Authorize(Policy = "OperationsRealtimeRead")]
public sealed class OperationsHub(OperationsCaseAccess access) : Hub
{
    public async Task JoinCase(Guid caseId)
    {
        if (!await access.CanReadAsync(caseId, Context.User!, Context.ConnectionAborted))
            throw new HubException("Not authorized for this operations case.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"operations:case:{caseId:N}");
    }

    public Task LeaveCase(Guid caseId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"operations:case:{caseId:N}");
}
