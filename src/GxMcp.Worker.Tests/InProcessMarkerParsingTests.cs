using System.Reflection;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // In-process builds forward the GeneXus section-marker protocol
    // (>S/>E0/>E1) rather than MSBuild.exe's "Specifying X..."/"Compiling"
    // text. HandleLine must parse the markers so the in-process path emits
    // phase progress and a named failure. Regression guard for the build-all
    // "no phases / never terminalizes" report against v2.25.1.
    public class InProcessMarkerParsingTests
    {
        private static void InvokeHandleLine(BuildService svc, BuildService.BuildTaskStatus status, string line, bool isError = false)
        {
            var mi = typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            mi.Invoke(svc, new object[] { status, line, isError });
        }

        [Theory]
        [InlineData("Specify", "Specifying")]
        [InlineData("Generate", "Generating")]
        [InlineData("Compilation", "Compiling")]
        [InlineData("Copying", "Finishing")]
        [InlineData("WebAppConfig", "Finishing")]
        [InlineData("DeveloperMenu", "Finishing")]
        [InlineData("Build", null)]        // outer wrapper — must not churn the phase
        [InlineData("Default", null)]      // unknown section — leave phase alone
        public void MapSectionToPhase_MapsKnownSections(string section, string expected)
        {
            Assert.Equal(expected, BuildService.MapSectionToPhase(section));
        }

        [Fact]
        public void HandleLine_SectionStartMarker_AdvancesPhase()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "sec-start", Phase = "Starting" };

            InvokeHandleLine(svc, status, ">SCompilation:-:Compiling the KB");

            Assert.Equal("Compiling", status.Phase);
        }

        [Fact]
        public void HandleLine_SectionStartMarker_UnknownSection_LeavesPhaseUnchanged()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "sec-unknown", Phase = "Generating" };

            InvokeHandleLine(svc, status, ">SDefault:-:Default model");

            Assert.Equal("Generating", status.Phase);
        }

        [Fact]
        public void HandleLine_SectionFailMarker_RecordsNamedPhaseFailure()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "sec-fail" };

            InvokeHandleLine(svc, status, ">E0Compilation:-:failed");

            Assert.NotNull(status.PhaseFailure);
            Assert.Equal("Compilation", status.PhaseFailure.Name);
        }

        [Fact]
        public void HandleLine_OuterBuildFailMarker_DoesNotSetPhaseFailure()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "outer-fail" };

            InvokeHandleLine(svc, status, ">E0Build");

            Assert.Null(status.PhaseFailure);
        }

        [Fact]
        public void HandleLine_SectionMarkers_AreNotCountedAsErrorsOrWarnings()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "no-miscount" };

            InvokeHandleLine(svc, status, ">SBuild");
            InvokeHandleLine(svc, status, ">SCompilation");
            InvokeHandleLine(svc, status, ">E0Compilation");
            InvokeHandleLine(svc, status, ">E1Copying");

            Assert.Equal(0, status.ErrorCount);
            Assert.Equal(0, status.WarningCount);
        }

        [Fact]
        public void HandleLine_AmbiguousObjectDiagnostic_IsCountedAsErrorEvenWithoutErrorPrefix()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "ambiguous-object" };

            InvokeHandleLine(svc, status, ">W'SampleEntity' é um nome Objeto ambíguo. Pode se referir a Table ou Transaction.");

            Assert.Equal(1, status.ErrorCount);
            Assert.Contains("ambíguo", status.Errors[0]);
        }

        private const string SpecErrorLine =
            ">O1spc0010: Type mismatch in assignment (Binary=File)|Artech.Architecture.Common.Location.SourcePosition, Artech.Architecture.Common, Version=11.0.0.0, Culture=neutral, PublicKeyToken=6f5bf81c27b6b8aa;"
            + "<SourcePosition><Line>2</Line><Char>0</Char><SelectionLength>0</SelectionLength><FullName>Procedure 'SampleProc'</FullName>"
            + "<ObjectType>84a12160--f59b--4ad7--a683--ea4481ac23e9</ObjectType><ObjectId>1</ObjectId><Part>00000000--0000--0000--0000--000000000000</Part></SourcePosition>";

        [Fact]
        public void HandleLine_InProcessErrorDiagnostic_IsCountedAndItemized()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "o1-error" };

            InvokeHandleLine(svc, status, ">SSpecification:-:Specification");
            InvokeHandleLine(svc, status, SpecErrorLine);
            InvokeHandleLine(svc, status, ">E0Specification:-:Specification");

            Assert.Equal(1, status.ErrorCount);
            Assert.Equal("error spc0010: Type mismatch in assignment (Binary=File) [SampleProc, line 2]", status.Errors[0]);
            Assert.Equal("SampleProc", status.ErrorsDetailed[0].gxObject);
            Assert.Equal("Specification", status.PhaseFailure.Name);
        }

        [Fact]
        public void HandleLine_InProcessErrorDiagnostic_ReachesSpecificationDiagnostics()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "o1-spec-diag" };

            InvokeHandleLine(svc, status, SpecErrorLine);

            var json = new Newtonsoft.Json.Linq.JObject
            {
                ["Status"] = "Failed",
                ["ErrorsDetailed"] = Newtonsoft.Json.Linq.JArray.FromObject(status.ErrorsDetailed)
            }.ToString();
            Assert.True(GxMcp.Worker.Helpers.SpecificationDiagnostics.HasSpecErrors(json));
            var diag = GxMcp.Worker.Helpers.SpecificationDiagnostics.Parse(json)[0];
            Assert.Equal("spc0010", diag["code"]?.ToString());
            Assert.Equal("SampleProc", diag["object"]?.ToString());
        }

        [Fact]
        public void HandleLine_InProcessWarningDiagnostic_IsCountedAsWarning()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "o2-warning" };

            InvokeHandleLine(svc, status, ">O2spc0024: Variable 'Unused' is not used.|Artech.Architecture.Common.Location.SourcePosition;<SourcePosition><Line>0</Line><FullName>Procedure 'SampleProc'</FullName></SourcePosition>");

            Assert.Equal(0, status.ErrorCount);
            Assert.Equal(1, status.WarningCount);
            Assert.Equal("warning spc0024: Variable 'Unused' is not used. [SampleProc]", status.Warnings[0]);
        }

        [Fact]
        public void HandleLine_InProcessUnreachableWarning_RecordsUnreachableTarget()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "o2-unreachable" };

            InvokeHandleLine(svc, status, ">O2spc0217: Object 'SampleProc' is not reachable from any main object.");

            Assert.Contains("SampleProc", status.UnreachableTargets);
        }

        [Theory]
        [InlineData(">L Specifying SampleProc ...")]
        [InlineData(">E0Specification:-:Specification")]
        [InlineData("O1spc0010: no protocol prefix")]
        [InlineData(">O3spc0010: unknown severity")]
        public void NormalizeInProcessDiagnostic_IgnoresOtherLines(string line)
        {
            Assert.Null(BuildService.NormalizeInProcessDiagnostic(line, out _));
        }
    }
}
