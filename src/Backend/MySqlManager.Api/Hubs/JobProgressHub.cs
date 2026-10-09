using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MySqlManager.Api.Hubs;

/// <summary>Live job progress. Clients join a job group and receive progress events from the worker via the database relay.</summary>
[Authorize]
public sealed class JobProgressHub : Hub
{
    /// <summary>Subscribe to progress for one job.</summary>
    public Task Subscribe(Guid jobId) => Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobId}");
}
