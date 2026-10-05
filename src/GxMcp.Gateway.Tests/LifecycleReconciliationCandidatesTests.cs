using System;
using System.Linq;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Xunit;

// issue #418: before admitting a serialized lifecycle job, only the scope's running jobs
// that hold a Worker task id are reconciled against the Worker.
public class LifecycleReconciliationCandidatesTests
{
    private static readonly OwnershipFence Fence = new OwnershipFence("session", "kb", 1);

    [Fact]
    public void CandidatesAreExactlyTheScopesRunningJobsWithAWorkerTask()
    {
        var registry = new BackgroundJobRegistry(600);
        var running = registry.AdmitLifecycle("s", "lifecycle/build", 60, Fence, "worker-a", "A").Job;
        var queued = registry.AdmitLifecycle("s", "lifecycle/build", 60, Fence, "worker-a", "B").Job;
        var otherScope = registry.AdmitLifecycle("s", "lifecycle/build", 60, Fence, "worker-b", "C").Job;
        registry.SetWorkerTaskId(running.Id, "task-1");
        registry.SetWorkerTaskId(otherScope.Id, "task-2");
        registry.SetWorkerTaskId(queued.Id, "task-3");

        Assert.Equal(new[] { running.Id }, registry.GetReconciliationCandidates("worker-a", Fence).Select(j => j.Id));
    }

    [Fact]
    public void RunningJobWithoutTaskIdIsNotACandidate()
    {
        var registry = new BackgroundJobRegistry(600);
        registry.AdmitLifecycle("s", "lifecycle/build", 60, Fence, "worker-a", "A");
        Assert.Empty(registry.GetReconciliationCandidates("worker-a", Fence));
    }

    [Fact]
    public async Task CompletingACandidatePromotesTheNextQueuedJob()
    {
        var registry = new BackgroundJobRegistry(600);
        var first = registry.AdmitLifecycle("s", "lifecycle/build", 60, Fence, "worker-a", "A").Job;
        var second = registry.AdmitLifecycle("s", "lifecycle/build", 60, Fence, "worker-a", "B").Job;
        registry.SetWorkerTaskId(first.Id, "task-1");

        foreach (var candidate in registry.GetReconciliationCandidates("worker-a", Fence))
            registry.Complete(candidate.Id, true, "worker confirmed");

        Assert.True(await registry.WaitForLifecycleAdmissionAsync(second.Id).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("running", registry.Get(second.Id)!.Status);
    }
}
