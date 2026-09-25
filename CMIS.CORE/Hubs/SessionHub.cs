using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CMIS.Core.Hubs;

[Authorize]
public class SessionHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var sessionId = Context.User?.FindFirst("sid")?.Value;

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                $"session:{sessionId}");
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var sessionId = Context.User?.FindFirst("sid")?.Value;

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                $"session:{sessionId}");
        }

        await base.OnDisconnectedAsync(exception);
    }
}