using API.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace API.Tests;

/// <summary>
/// An <see cref="IHubContext{THub}"/> that records what would have been broadcast, so the
/// things that publish to displays can be tested without a server or a client.
/// </summary>
public sealed class CapturingHubContext(Action<string, object?[]> onSend) : IHubContext<BubblesHub>
{
    public IHubClients Clients { get; } = new Hubs(onSend);

    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class Hubs(Action<string, object?[]> onSend) : IHubClients
    {
        public IClientProxy All { get; } = new Proxy(onSend);

        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => All;
        public IClientProxy Client(string connectionId) => All;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
        public IClientProxy Group(string groupName) => All;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => All;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
        public IClientProxy User(string userId) => All;
        public IClientProxy Users(IReadOnlyList<string> userIds) => All;
    }

    private sealed class Proxy(Action<string, object?[]> onSend) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            onSend(method, args);
            return Task.CompletedTask;
        }
    }
}
