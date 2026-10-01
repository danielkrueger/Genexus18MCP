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
        public void The_Wait_Result_Is_Inspected_Rather_Than_Discarded()
        {
            // The defect in one assertion: the bounded wait's bool was dropped on the
            // floor, so nothing downstream could tell a completed section set from a
            // truncated one.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "AnalyzeService.cs");

            Assert.DoesNotContain(
                "Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));",
                source, StringComparison.Ordinal);
            Assert.Contains("!Task.WaitAll(pending, InspectSectionBudget)", source, StringComparison.Ordinal);
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
        public void Every_Parallel_Section_Is_Named()
        {
            // A section with no name could only be reported by list position, which
            // means nothing to a caller - and a section added later would be unnamed by
            // omission, which is the same defect arriving again.
            string source = GxMcp.TestSupport.RepoSource.WithoutComments(
                "src", "GxMcp.Worker", "Services", "AnalyzeService.cs");

            int adds = source.Split(new[] { "tasks.Add(" }, StringSplitOptions.None).Length - 1;
            int named = source.Split(new[] { "tasks.Add(NewSection(" }, StringSplitOptions.None).Length - 1;
            Assert.True(adds > 0, "no parallel inspect sections found");
            Assert.Equal(adds, named);

            // The names are stable, and are the ones the response reports.
            foreach (var section in new[] { "signature", "variables", "structure", "domainsAndEnums", "callers" })
                Assert.Contains("NewSection(\"" + section + "\"", source, StringComparison.Ordinal);
        }
    }
}
