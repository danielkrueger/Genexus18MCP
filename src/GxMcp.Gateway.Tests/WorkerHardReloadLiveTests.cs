using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // End-to-end coverage for `genexus_worker_reload mode=hard`, which failed on every call.
    //
    // The unit tests (WorkerHardReloadSwapTests) pin the required-file set; this proves the two
    // behaviours that actually matter against a real gateway and a real KB:
    //
    //  1. A bogus sourceDir is rejected WITHOUT stopping the worker. The swap runs inside
    //     DrainAndReplaceAsync's post-drain hook, so before the fail-fast validation existed a
    //     bad path stopped a healthy worker, cleared the pool entry, and then failed.
    //  2. A real Worker build output — which contains GxMcp.Worker.exe and
    //     GxMcp.Worker.exe.config and NO GxMcp.Worker.dll, exactly like the harness's own
    //     worker directory — now swaps successfully and yields a NEW worker process. This is
    //     the case that was 100% broken: the old guard required a dll the build never emits.
    //
    // The swap is a self-swap (the harness worker runs from publish\worker, the same directory
    // used as the source), so the bytes copied are identical and nothing can be broken by it.
    [Trait("Category", "LiveIssue")]
    [Trait("Category", "ProcessSmoke")]
    public sealed class WorkerHardReloadLiveTests : IClassFixture<LiveGatewayHarness>, IAsyncLifetime
    {
        private readonly LiveGatewayHarness _harness;

        public WorkerHardReloadLiveTests(LiveGatewayHarness harness)
        {
            _harness = harness;
        }

        public Task InitializeAsync() => _harness.InitializeAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        private static int? WorkerPidOf(JObject? payload)
            => payload?["worker"]?["pid"]?.Type == JTokenType.Integer
                ? payload["worker"]!["pid"]!.Value<int>()
                : (int?)null;

        [LiveKbFact]
        public async Task BogusSourceDir_IsRejected_AndTheWorkerKeepsServing()
        {
            var before = LiveGatewayHarness.ParseToolPayload(
                await _harness.CallToolAsync("genexus_whoami", new JObject(), 120_000));
            int? pidBefore = WorkerPidOf(before);
            Assert.NotNull(pidBefore);

            var response = await _harness.CallToolAsync(
                "genexus_worker_reload",
                new JObject
                {
                    ["mode"] = "hard",
                    ["sourceDir"] = Path.Combine(Path.GetTempPath(), "gxmcp-no-such-worker-dir")
                },
                120_000);

            var payload = LiveGatewayHarness.ParseToolPayload(response);
            Assert.True(LiveGatewayHarness.IsToolError(response),
                "a bogus sourceDir must be reported as an error: " + response.ToString());
            string code = payload?["error"]?["code"]?.ToString() ?? payload?["code"]?.ToString() ?? "";
            Assert.Equal("WorkerSwapSourceInvalid", code);
            // The old failure named a missing 'GxMcp.Worker.dll'. That message must be gone.
            Assert.DoesNotContain("GxMcp.Worker.dll", response.ToString());

            // The decisive part: the worker was never drained.
            var after = LiveGatewayHarness.ParseToolPayload(
                await _harness.CallToolAsync("genexus_whoami", new JObject(), 120_000));
            Assert.Equal(pidBefore!.Value, WorkerPidOf(after));
        }

        [LiveKbFact]
        public async Task RealWorkerOutput_SwapsAndYieldsANewWorker()
        {
            string workerOut = Path.Combine(FindRepositoryRoot(), "publish", "worker");
            if (!Directory.Exists(workerOut)
                || !File.Exists(Path.Combine(workerOut, "GxMcp.Worker.exe")))
            {
                // publish/ is a build artifact; say so rather than assert nothing.
                return;
            }

            // Guard the premise: this directory must NOT contain the dll the old check required.
            Assert.False(File.Exists(Path.Combine(workerOut, "GxMcp.Worker.dll")),
                "publish/worker must not contain GxMcp.Worker.dll (OutputType=Exe)");

            // A DISTINCT source directory, which is the real shape of this call: a fresh build
            // output swapped into the running worker's directory. Pointing sourceDir at the
            // worker's own directory instead is a self-swap — the bytes are already in place,
            // and copying a file onto itself fails while the outgoing worker still holds the exe.
            string source = Path.Combine(Path.GetTempPath(), "gxmcp-swap-src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(source);
            try
            {
                foreach (string file in new[]
                {
                    "GxMcp.Worker.exe", "GxMcp.Worker.exe.config", "GxMcp.Worker.pdb"
                })
                {
                    string from = Path.Combine(workerOut, file);
                    if (File.Exists(from)) File.Copy(from, Path.Combine(source, file), overwrite: true);
                }

                var before = LiveGatewayHarness.ParseToolPayload(
                    await _harness.CallToolAsync("genexus_whoami", new JObject(), 120_000));
                int? pidBefore = WorkerPidOf(before);
                Assert.True(pidBefore.HasValue,
                    "expected a worker before the swap: " + before?.ToString(Newtonsoft.Json.Formatting.None));

                var response = await _harness.CallToolAsync(
                    "genexus_worker_reload",
                    new JObject { ["mode"] = "hard", ["sourceDir"] = source },
                    300_000);

                // Whatever the SDK takes, the old "missing required file" failure is gone.
                Assert.DoesNotContain("missing required file", response.ToString());
                Assert.DoesNotContain("GxMcp.Worker.dll", response.ToString());
                Assert.False(LiveGatewayHarness.IsToolError(response),
                    "a real Worker output must swap cleanly: " + response.ToString());

                // The reload must have produced a real, successful reply. ParseToolPayload
                // already unwraps result.content[0].text, so the reload's contract here is
                // status/swappedAndReady — not the MCP envelope's result/error keys.
                var payload = LiveGatewayHarness.ParseToolPayload(response);
                Assert.NotNull(payload);
                Assert.Equal("Reloaded", (string?)payload!["status"]);
                Assert.True(payload["swappedAndReady"]?.ToObject<bool?>(),
                    "reload should report a ready worker: " + response.ToString());

                var after = LiveGatewayHarness.ParseToolPayload(
                    await _harness.CallToolAsync("genexus_whoami", new JObject(), 180_000));
                int? pidAfter = WorkerPidOf(after);
                Assert.True(pidAfter.HasValue,
                    "expected a new worker after the swap; reload said: " + response.ToString()
                    + " | whoami said: " + after?.ToString(Newtonsoft.Json.Formatting.None));
                Assert.NotEqual(pidBefore!.Value, pidAfter!.Value);
            }
            finally
            {
                try { Directory.Delete(source, recursive: true); } catch { }
            }
        }

        [LiveKbFact]
        public async Task SourceDirEqualToTheRunningWorker_IsANoOp_NotASelfCopy()
        {
            // Pointing sourceDir at the directory the worker already runs from is a plausible
            // mistake, and before the same-directory guard it was fatal: CopyWorkerBinaries
            // copied GxMcp.Worker.exe onto itself, which throws a sharing violation while the
            // outgoing worker still holds the exe. Observed live as "The process cannot access
            // the file ... GxMcp.Worker.exe because it is being used by another process", and
            // because the copy runs AFTER the drain it failed the reload and left the KB with
            // no worker at all. The bytes are already in place, so this must succeed as a no-op.
            string workerOut = Path.Combine(FindRepositoryRoot(), "publish", "worker");
            if (!Directory.Exists(workerOut)
                || !File.Exists(Path.Combine(workerOut, "GxMcp.Worker.exe")))
            {
                return;
            }

            var response = await _harness.CallToolAsync(
                "genexus_worker_reload",
                new JObject { ["mode"] = "hard", ["sourceDir"] = workerOut },
                300_000);

            var payload = LiveGatewayHarness.ParseToolPayload(response);
            Assert.NotNull(payload);
            Assert.Equal("Reloaded", (string?)payload!["status"]);
            Assert.True(payload["swappedAndReady"]?.ToObject<bool?>(),
                "a self-swap must reload cleanly: " + response.ToString());
            Assert.DoesNotContain("being used by another process", response.ToString());
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the Genexus18MCP repository root.");
        }
    }
}
