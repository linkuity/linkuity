using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;

namespace Linkuity.Api.Infrastructure;

public class NoOpJobDispatcher : IJobDispatcher
{
    public Task DispatchAsync(Job job, CancellationToken ct = default) => Task.CompletedTask;
}
