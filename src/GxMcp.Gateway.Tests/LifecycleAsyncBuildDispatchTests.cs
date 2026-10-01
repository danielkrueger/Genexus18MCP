using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LifecycleAsyncBuildDispatchTests
    {
        /// <summary>
        /// Issue #359. This used to assert <c>RebuildAll</c> for
        /// <c>rebuild target=Customer</c> — encoding the defect as the expectation.
        /// The Worker's whole-KB branch emits <c>&lt;BuildAll/&gt;</c> without reading
        /// <c>targets</c>, so a targeted rebuild silently rebuilt the entire Knowledge
        /// Base and answered <c>Action: RebuildAll</c>.
        /// </summary>
        [Fact]
        public void BuildCommandFactory_UsesTargetedRebuild_WhenRebuildHasATarget()
        {
            var command = Program.BuildAsyncLifecycleCommand(
                "rebuild",
                new JObject { ["target"] = "Customer" },
                "job-2");

            Assert.Equal("Rebuild", command["action"]!.ToString());
            Assert.Equal("Customer", command["target"]!.ToString());
            Assert.Equal("job-2", command["cancelToken"]!.ToString());
        }

        [Fact]
        public void BuildCommandFactory_UsesRebuildAllForTargetlessRebuild()
        {
            // The documented whole-KB path, and what the reporter relied on when they did
            // not name a target.
            var command = Program.BuildAsyncLifecycleCommand(
                "rebuild",
                new JObject(),
                "job-2b");

            Assert.Equal("RebuildAll", command["action"]!.ToString());
        }

        [Fact]
        public void BuildCommandFactory_TreatsAWhitespaceTargetAsAbsent()
        {
            // Otherwise "  " would take the whole-KB branch while looking like a targeted
            // request, which is the original surprise in a different spelling.
            var command = Program.BuildAsyncLifecycleCommand(
                "rebuild",
                new JObject { ["target"] = "   " },
                "job-2c");

            Assert.Equal("RebuildAll", command["action"]!.ToString());
        }

        [Fact]
        public void BuildCommandFactory_UsesBuildAllForBuildAll()
        {
            var command = Program.BuildAsyncLifecycleCommand(
                "build_all",
                new JObject { ["dryRun"] = false },
                "job-3");

            Assert.Equal("BuildAll", command["action"]!.ToString());
            Assert.True(string.IsNullOrEmpty(command["target"]?.ToString()));
            Assert.Equal("job-3", command["cancelToken"]!.ToString());
        }

        [Fact]
        public void BuildCommandFactory_UsesSpecifyActionWhenWrappedAsync()
        {
            var command = Program.BuildAsyncLifecycleCommand(
                "specify",
                new JObject { ["target"] = "Customer" },
                "job-specify");

            Assert.Equal("Specify", command["action"]!.ToString());
            Assert.Equal("Customer", command["target"]!.ToString());
        }

        [Fact]
        public void SpecifyUsesAsyncOperationPathOnlyWhenWaitUntilDoneIsRequested()
        {
            Assert.True(Program.ShouldDispatchLifecycleBuildAsync(
                "genexus_lifecycle", "specify", new JObject { ["wait_until_done"] = true }));
            Assert.False(Program.ShouldDispatchLifecycleBuildAsync(
                "genexus_lifecycle", "specify", new JObject { ["wait_until_done"] = false }));
            Assert.False(Program.ShouldDispatchLifecycleBuildAsync(
                "genexus_lifecycle", "specify", new JObject { ["wait_until_done"] = true, ["dryRun"] = true }));
            Assert.False(Program.ShouldDispatchLifecycleBuildAsync(
                "genexus_lifecycle", "query", new JObject { ["wait_until_done"] = true }));
        }

        [Fact]
        public void LongActionsUseRegistryCommandRouting()
        {
            var validate = Program.BuildAsyncLifecycleCommand(
                "validate-kb", new JObject { ["limit"] = 7 }, "job-validate");
            Assert.Equal("KB", validate["module"]?.ToString());
            Assert.Equal("ValidateConditions", validate["action"]?.ToString());
            Assert.Equal(7, validate["limit"]?.ToObject<int>());

            var reorg = Program.BuildAsyncLifecycleCommand("reorg", new JObject(), "job-reorg");
            Assert.Equal("Reorg", reorg["action"]?.ToString());
            Assert.Equal("Build", reorg["module"]?.ToString());

            var index = Program.BuildAsyncLifecycleCommand(
                "index", new JObject { ["force"] = true }, "job-index");
            Assert.Equal("KB", index["module"]?.ToString());
            Assert.Equal("BulkIndex", index["action"]?.ToString());
            Assert.True(index["force"]?.ToObject<bool>());
            Assert.True(validate["queueLifecycle"]?.ToObject<bool>());
            Assert.True(reorg["queueLifecycle"]?.ToObject<bool>());
            Assert.True(index["queueLifecycle"]?.ToObject<bool>());
        }

        [Fact]
        public void WorkerLifecycleStatusCommand_PreservesAliasCorrelationAndWaitCursor()
        {
            var registry = new BackgroundJobRegistry(600);
            var admitted = registry.AdmitLifecycle(
                "session", "lifecycle/build", 60,
                new OwnershipFence("session", "kb", 1), "worker", "A");
            admitted.Job.WorkerTaskId = "abcd1234";

            var command = Program.BuildWorkerLifecycleStatusCommand(
                admitted.Job,
                new JObject { ["since"] = "warnings:2", ["pageSize"] = 25 },
                waitSeconds: 30,
                until: "change");

            Assert.Equal("Build", command["module"]?.ToString());
            Assert.Equal("Status", command["action"]?.ToString());
            Assert.Equal("abcd1234", command["target"]?.ToString());
            Assert.Equal(30, command["wait"]?.ToObject<int>());
            Assert.Equal("change", command["until"]?.ToString());
            Assert.Equal("warnings:2", command["since"]?.ToString());
            Assert.Equal(25, command["pageSize"]?.ToObject<int>());
        }

        [Fact]
        public void LongActionsRemainAsyncWhenWaitUntilDoneOrSmallEstimateIsRequested()
        {
            Assert.True(Program.ShouldDispatchLifecycleBuildAsync(
                "genexus_lifecycle", "validate-kb", new JObject()));
            Assert.True(Program.ShouldDispatchLifecycleBuildAsync(
                "genexus_lifecycle", "reorg", new JObject()));
            Assert.True(Program.ShouldDispatchLifecycleBuildAsync(
                "genexus_lifecycle", "index", new JObject { ["force"] = true }));
            Assert.True(Program.ShouldDispatchLifecycleBuildAsync(
                "genexus_lifecycle", "build", new JObject { ["wait_until_done"] = true }));
        }
    }
}
