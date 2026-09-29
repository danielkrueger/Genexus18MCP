using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #325: a synchronous <c>genexus_versioning history_get</c> can outlast the client's
    /// window, and the reply carrying the operationId is then the reply that never arrives - so
    /// the caller has no documented handle to a result the Gateway had in fact stored. These
    /// tests pin the accept-then-poll contract that closes that gap, and pin the boundary that
    /// keeps it off the KB-mutating actions.
    /// </summary>
    public class AsyncLongReadContractTests
    {
        [Theory]
        [InlineData("history_get")]
        [InlineData("history_list")]
        [InlineData("diff")]
        [InlineData("diff_generated")]
        [InlineData("blame")]
        public void IsAsyncLongReadAction_AcceptsVersionStoreReads(string action)
        {
            Assert.True(Program.IsAsyncLongReadAction("genexus_versioning", new JObject
            {
                ["action"] = action,
                ["async"] = true
            }));
        }

        [Fact]
        public void IsAsyncLongReadAction_IgnoresMixedCaseAndPaddedAction()
        {
            Assert.True(Program.IsAsyncLongReadAction("Genexus_Versioning", new JObject
            {
                ["action"] = "  History_Get  ",
                ["async"] = true
            }));
        }

        // Without async=true the call must stay synchronous: the async path is opt-in because it
        // changes when the answer arrives, not what it is.
        [Fact]
        public void IsAsyncLongReadAction_RequiresExplicitOptIn()
        {
            Assert.False(Program.IsAsyncLongReadAction("genexus_versioning", new JObject
            {
                ["action"] = "history_get"
            }));
            Assert.False(Program.IsAsyncLongReadAction("genexus_versioning", new JObject
            {
                ["action"] = "history_get",
                ["async"] = false
            }));
            Assert.False(Program.IsAsyncLongReadAction("genexus_versioning", null));
        }

        // The mutating actions keep the mutation path (recovery fences assume a write may have
        // persisted) and every other tool keeps its own contract.
        [Theory]
        [InlineData("history_save")]
        [InlineData("history_restore")]
        [InlineData("undo")]
        [InlineData("time_travel")]
        public void IsAsyncLongReadAction_RefusesKbMutatingActions(string action)
        {
            Assert.False(Program.IsAsyncLongReadAction("genexus_versioning", new JObject
            {
                ["action"] = action,
                ["async"] = true
            }));
        }

        [Theory]
        [InlineData("genexus_read")]
        [InlineData("genexus_edit")]
        [InlineData("genexus_query")]
        public void IsAsyncLongReadAction_DoesNotClaimOtherTools(string toolName)
        {
            Assert.False(Program.IsAsyncLongReadAction(toolName, new JObject
            {
                ["action"] = "history_get",
                ["async"] = true
            }));
        }

        [Fact]
        public void BuildAsyncReadAcceptedPayload_ReturnsTheHandleBeforeTheWork()
        {
            var job = new JobEntry
            {
                Id = "0123456789abcdef0123456789abcdef",
                EstimatedSeconds = 60
            };

            JObject payload = Program.BuildAsyncReadAcceptedPayload(job, "history_get");

            // The identifier must be usable exactly as documented: bare id, job_id and the
            // op: alias genexus_lifecycle status/result resolve.
            Assert.Equal(job.Id, payload["operationId"]?.ToString());
            Assert.Equal(job.Id, payload["job_id"]?.ToString());
            Assert.Equal("op:" + job.Id, payload["pollTarget"]?.ToString());
            Assert.Equal("running", payload["status"]?.ToString());
            Assert.Equal(60, payload["estimated_seconds"]?.ToObject<int>());
            Assert.Equal("history_get", payload["action"]?.ToString());

            // The instruction that makes the result reachable must name the retrieval call and
            // state that the read survives this turn - that is the whole point of the path.
            string hint = payload["hint"]?.ToString() ?? string.Empty;
            Assert.Contains("genexus_lifecycle", hint);
            Assert.Contains("op:" + job.Id, hint);
            Assert.Contains("result", hint);
        }

        [Fact]
        public void BuildAsyncReadAcceptedPayload_RejectsMissingJob()
        {
            Assert.Throws<ArgumentNullException>(() => Program.BuildAsyncReadAcceptedPayload(null!, "history_get"));
        }

        // The schema is the contract an MCP client reads before it ever sees the help entry, and
        // the gateway's argument guard refuses an undeclared key that looks like a typo of a
        // declared one. An undeclared `async` would therefore be a latent rejection.
        [Fact]
        public void VersioningSchema_DeclaresTheAsyncReadArguments()
        {
            var result = GatewayArgsValidator.Validate("genexus_versioning", new JObject
            {
                ["action"] = "history_get",
                ["async"] = true,
                ["estimated_seconds"] = 90
            });

            Assert.True(
                result.Ok,
                "async/estimated_seconds on history_get must pass schema pre-validation: "
                + string.Join("; ", result.Violations.Select(v => $"{v.Path} ({v.Actual})")));
        }
    }
}
