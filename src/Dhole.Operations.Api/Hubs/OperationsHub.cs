using Dhole.Operations.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Dhole.Operations.Api.Hubs;

[Authorize]
public sealed class OperationsHub(OperationsCaseAccess access) : Hub
{
    // Dashboard access is identical to the REST dashboard. Groups carry only
    // a data-free invalidation; the REST endpoint enforces individual/team ACLs.
    public async Task JoinDashboard()
    {
        var user = Context.User!;
        if (OperationsCaseAccess.UserId(user) is null ||
            !OperationsCaseAccess.HasScope(user, "operations.case.read"))
            throw new HubException("Not authorized for the operations dashboard.");
        if (OperationsCaseAccess.IsSystemAdministrator(user))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "operations:dashboard:admin");
            return;
        }
        var company = await access.ResolveCompanyIdAsync(user, Context.ConnectionAborted);
        if (!company.HasValue)
            throw new HubException("Operations company could not be resolved.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"operations:dashboard:{company.Value:N}");
    }

    [Authorize(Policy = "OperationsRealtimeRead")]
    public async Task JoinCase(Guid caseId)
    {
        if (!await access.CanReadAsync(caseId, Context.User!, Context.ConnectionAborted))
            throw new HubException("Not authorized for this operations case.");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"operations:case:{caseId:N}");
    }

    [Authorize(Policy = "OperationsRealtimeRead")]
    public Task LeaveCase(Guid caseId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"operations:case:{caseId:N}");
}
