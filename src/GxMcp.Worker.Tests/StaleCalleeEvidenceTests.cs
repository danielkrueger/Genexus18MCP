using System;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #411: a callee outside the build keeps an old generated .cs; the build should say so.
    public class StaleCalleeEvidenceTests
    {
        private static readonly DateTime Edited = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

        private static JArray Find(string callee, bool found, DateTime? generatedUtc)
            => BuildService.FindStaleCallees(
                new[] { callee },
                _ => Edited,
                _ => new GeneratedDiffService.GeneratedFileEvidence
                {
                    Target = callee,
                    Found = found,
                    FreshestPath = "web/" + callee + ".cs",
                    FreshestWriteUtc = generatedUtc
                });

        [Fact]
        public void CalleeGeneratedBeforeItsLastEditIsReportedWithBothTimestamps()
        {
            var stale = Find("InternalCallee", true, Edited.AddHours(-1));

            var entry = Assert.Single(stale);
            Assert.Equal("InternalCallee", (string)entry["object"]);
            Assert.Equal("web/InternalCallee.cs", (string)entry["path"]);
            Assert.Equal("2026-05-01T11:00:00Z", (string)entry["generatedUtc"]);
            Assert.Equal("2026-05-01T12:00:00Z", (string)entry["lastUpdateUtc"]);
        }

        [Fact]
        public void FreshCalleeIsNotReported()
        {
            Assert.Empty(Find("InternalCallee", true, Edited.AddMinutes(1)));
        }

        [Fact]
        public void CalleeWithNoGeneratedFileBelongsToGenerateGapAndIsNotReported()
        {
            Assert.Empty(Find("InternalCallee", false, null));
        }

        [Fact]
        public void CalleeWithoutIndexedEditTimeIsNotReported()
        {
            var stale = BuildService.FindStaleCallees(
                new[] { "InternalCallee" },
                _ => null,
                _ => new GeneratedDiffService.GeneratedFileEvidence { Found = true, FreshestWriteUtc = Edited.AddDays(-9) });

            Assert.Empty(stale);
        }
    }
}
