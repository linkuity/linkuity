using Linkuity.Core.Interfaces;
using Linkuity.Core.Models;

namespace Linkuity.Api.Tests.TestDoubles;

public class CapturingJobDispatcher : IJobDispatcher
{
    private readonly List<Guid> _dispatched = new();

    public IReadOnlyList<Guid> Dispatched => _dispatched;

    public Task DispatchAsync(Job job, CancellationToken ct = default)
    {
        _dispatched.Add(job.Id);
        return Task.CompletedTask;
    }
}
