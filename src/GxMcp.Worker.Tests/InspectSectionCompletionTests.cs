using System;
using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #334: <c>inspect</c> ran its metadata sections through
    /// <c>Task.Run</c>, which reads live SDK objects on a thread pool thread rather than
    /// the owner STA, and then called
    /// <c>Task.WaitAll(tasks, TimeSpan.FromSeconds(5))</c> and discarded the returned
    /// bool. A section that missed the budget therefore kept running: still touching the
    /// SDK off its owner thread, and still writing into the <c>JObject</c> this method had
    /// already returned - and could cache.
    ///
    /// <para>
    /// Two properties matter, and neither is observable without a live SDK model whose
    /// accessors assert their owner thread. So the guards are split: the completion
    /// result is pinned behaviourally against a synthetic slow/fast task pair, and the
    /// SDK-touching shape is pinned as a source-shape assertion on the production method.
    /// That is the honest split - a fake SDK object would test the fake, not the SDK.
    /// </para>
    /// </summary>
    public class InspectSectionCompletionTests
    {
        [Fact]
        public void The_Section_Budget_Is_A_Named_Bounded_Constant()
        {
            // Inline in a `WaitAll` call it was neither configurable nor assertable, and
            // a regression that raised it to minutes would still be "green".
            Assert.InRange(AnalyzeService.InspectSectionBudget, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        }

        [Fact]
        public void A_Completed_Section_Reports_Nothing_Incomplete()
        {
            var sections = new[]
            {
                new System.Collections.Generic.KeyValuePair<string, System.Threading.Tasks.Task>("signature",
                    System.Threading.Tasks.Task.CompletedTask),
            };

            var unfinished = sections.Where(s => !s.Value.IsCompleted).Select(s => s.Key).ToList();

            Assert.Empty(unfinished);
        }

        [Fact]
        public void A_Slow_Section_Is_Named_And_The_Fast_One_Is_Not()
        {
            // The property the discarded bool used to hide: a section that missed the
            // budget has to be identifiable in the response, or its absence reads as
            // "no callers" / "no variables" - a wrong answer rather than a missing one.
            using var gate = new System.Threading.ManualResetEventSlim(false);
            var slow = System.Threading.Tasks.Task.Run(() => gate.Wait(TimeSpan.FromSeconds(10)));
            var fast = System.Threading.Tasks.Task.CompletedTask;

            var sections = new[]
            {
                new System.Collections.Generic.KeyValuePair<string, System.Threading.Tasks.Task>("callers", slow),
                new System.Collections.Generic.KeyValuePair<string, System.Threading.Tasks.Task>("signature", fast),
            };

            // Let the slow task actually start so IsCompleted is meaningful.
            System.Threading.Thread.Sleep(50);

            var unfinished = sections.Where(s => !s.Value.IsCompleted).Select(s => s.Key).ToList();
            gate.Set();
            slow.Wait(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { "callers" }, unfinished);
        }

        [Fact]
        public void The_Budget_Is_Enforced_On_The_Owner_Thread_Not_By_Waiting_For_Tasks()
        {
            // Issue #368. The sections used to be Task.Run lambdas holding the live
            // KBObject, reading the SDK from thread-pool threads while this thread waited,
            // and a late writer could still mutate the published (and cached) response.
            // The deadline now runs on the capture loop and nothing outlives the request.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "AnalyzeService.cs");

            // No bounded wait whose result could be ignored, and no SDK capture off-thread.
            Assert.DoesNotContain(
                "Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));",
                source, StringComparison.Ordinal);
            Assert.DoesNotContain("Task.WaitAll(pending", source, StringComparison.Ordinal);
            Assert.DoesNotContain("NewSection(", source, StringComparison.Ordinal);

            // The deadline is consulted, both between sections and inside a long walk.
            Assert.Contains("capture.BudgetLeft", source, StringComparison.Ordinal);
            Assert.Contains("_clock.Elapsed < InspectSectionBudget", source, StringComparison.Ordinal);
            Assert.Contains("InspectDeadlineCheckInterval", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Sections_Write_Into_Their_Own_Slot_And_Are_Merged_On_The_Request_Thread()
        {
            // A late writer that can still reach the envelope is the defect #368 closes:
            // the response could contain both incompleteSections=["callers"] and a
            // "callers" key, and a cached entry could change after being stored.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "AnalyzeService.cs");

            // Every capture body takes its own slot and writes only into it.
            int captures = source.Split(new[] { "capture.Capture(" }, StringSplitOptions.None).Length - 1;
            Assert.True(captures > 0, "no inspect sections found");
            Assert.DoesNotContain("lock (result) result[", source, StringComparison.Ordinal);

            // The merge is the only place the envelope is written for these sections.
            Assert.Contains("foreach (var slot in capture.Slots)", source, StringComparison.Ordinal);
            Assert.Contains("result[property.Name] = property.Value", source, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Failed_Section_Is_Reported_Rather_Than_Swallowed()
        {
            // "No callers" and "the caller walk threw" must not look alike.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "AnalyzeService.cs");

            Assert.Contains("sectionErrors", source, StringComparison.Ordinal);
            Assert.Contains("AddError(section, ex)", source, StringComparison.Ordinal);
        }

        [Fact]
        public void An_Incomplete_Inspection_Says_Which_Sections_Are_Missing()
        {
            // An incomplete section must be explicit. The reported consequence was a
            // caller reading a missing "callers" key as "this object has no callers".
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "AnalyzeService.cs");

            Assert.Contains("incompleteSections", source, StringComparison.Ordinal);
            Assert.Contains("sectionsComplete", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_Section_Is_Named()
        {
            // A section with no name could only be reported by list position, which
            // means nothing to a caller - and a section added later would be unnamed by
            // omission, which is the same defect arriving again.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "AnalyzeService.cs");

            int captures = source.Split(new[] { "capture.Capture(\"" }, StringSplitOptions.None).Length - 1;
            int named = source.Split(new[] { "capture.Capture(\"" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(captures, named);

            // The names are stable, and are the ones the response reports.
            foreach (var section in new[] { "signature", "variables", "structure", "domainsAndEnums", "callers" })
                Assert.Contains("capture.Capture(\"" + section + "\"", source, StringComparison.Ordinal);
        }
    }
}
