using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The ObjectNotFound envelope used to be copy-pasted at each call site
    /// (10x in LayoutService, 6x + 2x in HistoryService). It is now built once per
    /// surface. These tests pin the resulting wire contract so an edit to a
    /// shared builder cannot silently change what an LLM client recovers from,
    /// and so the remaining hand-written sites cannot drift away from them.
    /// </summary>
    public class ObjectNotFoundEnvelopeTests
    {
        [Fact]
        public void HistorySnapshot_CarriesReListAndReindexRecoverySteps()
        {
            var json = JObject.Parse(HistoryService.SnapshotObjectNotFound("MyPanel"));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("MyPanel", json["target"]?.ToString());
            Assert.Equal("ObjectNotFound", json["error"]?["code"]?.ToString());
            Assert.Equal("Object not found.", json["error"]?["message"]?.ToString());
            Assert.Equal("Verify the object name and ensure the KB is open.",
                json["error"]?["hint"]?.ToString());

            var steps = (JArray)json["error"]?["nextSteps"];
            Assert.NotNull(steps);
            Assert.Equal(2, steps.Count);
            Assert.Equal("genexus_list_objects", steps[0]?["tool"]?.ToString());
            Assert.Equal("MyPanel", steps[0]?["args"]?["name_contains"]?.ToString());
            Assert.Equal("genexus_lifecycle", steps[1]?["tool"]?.ToString());
            Assert.Equal("index", steps[1]?["args"]?["action"]?.ToString());
            Assert.True(steps[1]?["args"]?["force"]?.ToObject<bool>());
        }

        [Fact]
        public void HistoryRevision_UsesTheKbScopedSingleListStep()
        {
            var json = JObject.Parse(HistoryService.RevisionObjectNotFound("MyTxn"));

            Assert.Equal("MyTxn", json["target"]?.ToString());
            Assert.Equal("ObjectNotFound", json["error"]?["code"]?.ToString());
            Assert.Equal("The requested object is not available in the active Knowledge Base.",
                json["error"]?["hint"]?.ToString());

            var steps = (JArray)json["error"]?["nextSteps"];
            Assert.NotNull(steps);
            Assert.Single(steps);
            Assert.Equal("genexus_list_objects", steps[0]?["tool"]?.ToString());
        }

        [Fact]
        public void HistoryBuilders_StayDistinct()
        {
            // The two surfaces name different KB state in the hint; collapsing them
            // would silently change the recovery guidance for one of them.
            Assert.NotEqual(
                JObject.Parse(HistoryService.SnapshotObjectNotFound("X"))["error"]?["hint"]?.ToString(),
                JObject.Parse(HistoryService.RevisionObjectNotFound("X"))["error"]?["hint"]?.ToString());
        }

        [Fact]
        public void LayoutVisual_CarriesListAllStep()
        {
            var json = JObject.Parse(LayoutService.VisualObjectNotFound("MyReport"));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("MyReport", json["target"]?.ToString());
            Assert.Equal("ObjectNotFound", json["error"]?["code"]?.ToString());
            Assert.Equal("Verify the object name matches an entry in the active Knowledge Base.",
                json["error"]?["hint"]?.ToString());

            var steps = (JArray)json["error"]?["nextSteps"];
            Assert.NotNull(steps);
            Assert.Single(steps);
            Assert.Equal("genexus_list_objects", steps[0]?["tool"]?.ToString());
            Assert.Equal("Lists all objects in the KB so you can confirm the correct name.",
                steps[0]?["why"]?.ToString());
        }

        [Fact]
        public void EveryBuilder_EmitsACuratedRecoverableEnvelope()
        {
            // NextStepsCurationGuardTests reads the *source* at each emission site;
            // these builders moved those sites behind a method call, so this
            // asserts the equivalent invariant on the produced envelopes.
            string[] envelopes =
            {
                HistoryService.SnapshotObjectNotFound("T"),
                HistoryService.RevisionObjectNotFound("T"),
                LayoutService.VisualObjectNotFound("T"),
            };

            foreach (string raw in envelopes)
            {
                var error = JObject.Parse(raw)["error"];
                Assert.Equal("ObjectNotFound", error?["code"]?.ToString());
                Assert.False(string.IsNullOrWhiteSpace(error?["hint"]?.ToString()));

                var steps = (JArray)error?["nextSteps"];
                Assert.NotNull(steps);
                Assert.NotEmpty(steps);
                foreach (var step in steps.OfType<JObject>())
                {
                    Assert.False(string.IsNullOrWhiteSpace(step["tool"]?.ToString()));
                    Assert.False(string.IsNullOrWhiteSpace(step["why"]?.ToString()));
                }
            }
        }
    }
}
