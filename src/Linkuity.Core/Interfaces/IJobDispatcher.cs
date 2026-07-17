using Linkuity.Core.Models;

namespace Linkuity.Core.Interfaces;

public interface IJobDispatcher
{
    Task DispatchAsync(Job job, CancellationToken ct = default);
}
