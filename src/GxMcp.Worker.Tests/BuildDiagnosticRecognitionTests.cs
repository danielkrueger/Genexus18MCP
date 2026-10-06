using System;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #409, two independent defects that both made a build report less than it
    /// actually knew.
    ///
    /// <para>
    /// <b>Dropped severity.</b> <c>BuildService.HandleLine</c> receives an <c>isError</c>
    /// flag and never reads it: severity is re-derived from text regexes, and both
    /// <c>_rxError</c> and <c>_rxSpecError</c> require a literal <c>error</c>. The in-process
    /// engine routes <em>every</em> message through <c>LogMessageEvent</c>, which passes
    /// <c>isError: false</c> and cannot tell a diagnostic from progress chatter - so the
    /// severity the BL engine knows is discarded on the way in. A <c>spc0031</c> line was
    /// pushed into <c>TailLines</c> and never counted, so <c>specify</c> returned
    /// <c>errorCount 0</c> for a run that had failed, and the same loss produced
    /// "compiler diagnostics were not captured" on the <c>includeCallees=none</c> route.
    ///
    /// <para>
    /// <b>The generate-gap warning.</b> The freshness probe globbed <c>target + ".*"</c> and
    /// then required an exact filename match. The .NET generator prefixes a Main object's
    /// source with <c>a</c>, so <c>rcliser2sql</c> exists only as <c>arcliser2sql.cs</c>:
    /// the probe could not see it, reported it missing, and every Main report build raised a
    /// false warning.
    /// </para>
    /// </summary>
    public class BuildDiagnosticRecognitionTests
    {
        // ── Dropped severity ──────────────────────────────────────────────────



        private static void InvokeHandleLine(
            BuildService service, BuildService.BuildTaskStatus status, string line, bool isError)
        {
            var method = typeof(BuildService).GetMethod(
                "HandleLine",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            method.Invoke(service, new object[] { status, line, isError });
        }

        [Fact]
        public void A_Bare_GeneXus_Diagnostic_Is_Counted_As_An_Error()
        {
            // The reported symptom: spc0031 present in the output, errorCount 0.
            var service = new BuildService();
            var status = new BuildService.BuildTaskStatus { Action = "BuildOne" };
            InvokeHandleLine(service, status, "spc0031 No relationship found among attributes in group", false);

            Assert.Equal(1, status.ErrorCount);
            Assert.Contains(status.Errors, e => e.Contains("spc0031"));
        }

        [Theory]
        [InlineData("gen0005 Unknown attribute")]
        [InlineData("src0042 Invalid source")]
        [InlineData("qry0011 Bad query reference")]
        public void Every_GeneXus_Diagnostic_Prefix_Is_Recognized(string line)
        {
            var service = new BuildService();
            var status = new BuildService.BuildTaskStatus { Action = "BuildOne" };
            InvokeHandleLine(service, status, line, false);

            Assert.Equal(1, status.ErrorCount);
        }

        [Fact]
        public void The_Existing_Error_Forms_Keep_Working()
        {
            foreach (var line in new[]
            {
                "error CS0246: The type or namespace name 'pcliserpg' could not be found",
                "error spc0031: No relationship found",
            })
            {
                var service = new BuildService();
                var status = new BuildService.BuildTaskStatus { Action = "BuildOne" };
                InvokeHandleLine(service, status, line, false);
                Assert.Equal(1, status.ErrorCount);
            }
        }

        [Theory]
        [InlineData("Generating MyObject to C:\\kb\\MyObject.cs")]
        [InlineData("Specifying MyObject...")]
        [InlineData("Compiling MyObject")]
        [InlineData("Module copy: 12 files")]
        [InlineData("the error is in the source")]
        public void Ordinary_Output_Is_Not_Mistaken_For_A_Diagnostic(string line)
        {
            // The negative direction matters more than the positive one here: promoting
            // progress chatter into errors would turn a passing build into a failing one.
            var service = new BuildService();
            var status = new BuildService.BuildTaskStatus { Action = "BuildOne" };
            InvokeHandleLine(service, status, line, false);

            Assert.Equal(0, status.ErrorCount);
        }

        [Fact]
        public void Spc0217_Is_Not_Promoted_Because_The_Code_Treats_It_As_An_Expected_Notice()
        {
            // "Unreachable" is how the specifier reports an object it deliberately skipped
            // generating. BuildService already treats a missing .cs with spc0217 as
            // "expected, not a gap", so counting it as an error would make a successful
            // build report failure.
            var service = new BuildService();
            var status = new BuildService.BuildTaskStatus { Action = "BuildOne" };
            InvokeHandleLine(service, status, "spc0217 Object is unreachable", false);

            Assert.Equal(0, status.ErrorCount);
        }

        [Fact]
        public void The_Diagnostic_Still_Reaches_Tail_Lines_For_Context()
        {
            var service = new BuildService();
            var status = new BuildService.BuildTaskStatus { Action = "BuildOne" };
            InvokeHandleLine(service, status, "spc0031 No relationship found among attributes", false);

            Assert.Contains(status.TailLines, l => l.Contains("spc0031"));
        }

        // ── generate-gap filename ─────────────────────────────────────────────

        [Theory]
        [InlineData("C:\\kb\\arcliser2sql.cs", "rcliser2sql", true)]
        [InlineData("C:\\kb\\rcliser2sql.cs", "rcliser2sql", true)]
        [InlineData("C:\\kb\\ARCLISER2SQL.CS", "rcliser2sql", true)]
        [InlineData("C:\\kb\\rcliser2sql.aspx", "rcliser2sql", true)]
        [InlineData("C:\\kb\\arcliser2sqlxy.cs", "rcliser2sql", false)]
        [InlineData("C:\\kb\\brcliser2sql.cs", "rcliser2sql", false)]
        [InlineData("C:\\kb\\rcliser2sql.txt", "rcliser2sql", false)]
        public void The_Main_Prefix_Is_Accepted_But_A_Sibling_Name_Is_Not(
            string file, string target, bool expected)
        {
            Assert.Equal(expected, GeneratedDiffService.IsGeneratedFileFor(file, target));
        }
    }
}
