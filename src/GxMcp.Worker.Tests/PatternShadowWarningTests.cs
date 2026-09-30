using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The pattern-shadow advisory was built twice - as a private
    /// <c>BuildPatternShadowWarningsIfAny</c> on <c>PatchService</c> and on
    /// <c>WriteService.WriteVisualPart</c> - and the two had already drifted: both
    /// emitted the same <c>EditingWebFormUnderPattern</c> code and the same
    /// severity, with a different sentence for the same advice. One told the agent
    /// to edit <c>part=PatternInstance</c> on the object, the other named the
    /// <c>genexus_edit</c> call that does it. A client cannot tell those apart,
    /// because the code is the same.
    ///
    /// It is now one method on <c>PatternAnalysisService</c>, which already owns
    /// <c>ResolveWWPInstance</c> and the object lookup. The callers keep their own
    /// object resolution, which genuinely differs: <c>PatchService</c> starts from a
    /// target plus a type filter, <c>WriteService</c> from an object it already has.
    ///
    /// These pin the envelope rather than the probe, because the probe needs a live
    /// Knowledge Base: what must not drift is the warning a client parses.
    /// </summary>
    public class PatternShadowWarningTests
    {
        [Fact]
        public void BothCallersReachTheOneProbe()
        {
            foreach (string file in new[] { "PatchService.cs", "WriteService.VisualWrite.cs" })
            {
                string src = RepoSource.Read("src", "GxMcp.Worker", "Services", file);

                Assert.Contains("_patternAnalysisService.BuildPatternShadowWarning(", src);
                // Neither caller may rebuild the advisory it was handed. Scoped to
                // the construction rather than the code name, which the explanatory
                // comments legitimately mention.
                Assert.DoesNotContain("[\"code\"] = \"EditingWebFormUnderPattern\"", src);
                Assert.DoesNotContain("candidatePrefixes", src);
            }
        }

        [Fact]
        public void TheProbeIsDefinedOnceAndKeepsItsEscapeHatch()
        {
            string probe = RepoSource.Read("src", "GxMcp.Worker", "Services", "PatternAnalysisService.cs");

            Assert.Equal(1, SourceAssert.Count(probe, "internal JArray BuildPatternShadowWarning("));
            Assert.Contains("ResolveWWPInstance(obj)", probe);
            Assert.Contains("FindPatternPart(resolved, \"PatternInstance\")", probe);
            Assert.Contains("IsVisualPart(partName)", probe);
            // Best-effort by contract: a probe that throws would fail the write it
            // is only meant to advise on.
            Assert.Contains("catch (Exception ex)", probe);

            Assert.Equal(0, SourceAssert.Count(probe, "private JArray BuildPatternShadowWarningsIfAny("));
        }

        [Fact]
        public void EachCallerStillResolvesItsOwnObject()
        {
            // The one real difference between the two sites, and the reason the
            // helper takes an object rather than a target. Scoped to each caller's
            // own method, not the whole file, which is large and does other lookups.
            string patch = SourceAssert.MethodBody(RepoSource.Read("src", "GxMcp.Worker", "Services", "PatchService.cs"), "private JArray BuildPatternShadowWarningsIfAny(");
            string visual = SourceAssert.MethodBody(RepoSource.Read("src", "GxMcp.Worker", "Services", "WriteService.VisualWrite.cs"), "private JArray BuildPatternShadowWarningsIfAny(");

            // PatchService resolves from a target and a type filter...
            Assert.Contains("_objectService.FindObject(target, typeFilter)", patch);
            // ...and WriteService is handed the object it already had.
            Assert.DoesNotContain("FindObject", visual);
            Assert.Contains("global::Artech.Architecture.Common.Objects.KBObject obj", visual);
        }

        [Fact]
        public void TheWarningIsOmittedWhenTheObjectIsNotPatternCovered()
        {
            // The probe's own guard, pinned as a shape: a caller that passes a null
            // object gets nothing rather than an exception.
            string probe = RepoSource.Read("src", "GxMcp.Worker", "Services", "PatternAnalysisService.cs");

            Assert.Contains("if (obj == null) return null;", probe);
            Assert.Contains("if (resolved == null) return null;", probe);
            Assert.Contains("if (part == null) return null;", probe);
        }

        /// <summary>
        /// The advisory's message expression, read from the probe's own source.
        ///
        /// It is asserted against the real text rather than a local copy of it. A
        /// reconstruction would let the probe's wording drift while the test stayed
        /// green - which is exactly the failure this consolidation exists to stop,
        /// since the two copies had drifted into different sentences behind one
        /// warning code.
        ///
        /// The probe cannot be invoked here: it needs a live Knowledge Base, and the
        /// message is assembled by concatenating SDK-derived names into it.
        /// </summary>
        private static string WarningMessageSource()
        {
            // Scoped to the probe's own body first. PatternAnalysisService emits
            // several unrelated ["message"] values, so searching the whole file
            // would assert against whichever came first - which is how this test
            // briefly passed against a message that had nothing to do with the
            // advisory.
            string probe = SourceAssert.MethodBody(
                RepoSource.Read("src", "GxMcp.Worker", "Services", "PatternAnalysisService.cs"),
                "internal JArray BuildPatternShadowWarning(");

            int at = probe.IndexOf("[\"message\"] =", StringComparison.Ordinal);
            Assert.True(at >= 0, "the advisory no longer sets a message");

            int end = probe.IndexOf("[\"patternInstance\"]", at, StringComparison.Ordinal);
            Assert.True(end > at, "the advisory no longer ends its message with patternInstance");

            return probe.Substring(at, end - at);
        }

        [Fact]
        public void TheAdvisoryCarriesTheStableEnvelope()
        {
            string probe = SourceAssert.MethodBody(
                RepoSource.Read("src", "GxMcp.Worker", "Services", "PatternAnalysisService.cs"),
                "internal JArray BuildPatternShadowWarning(");

            Assert.Contains("return new JArray", probe);
            Assert.Contains("[\"code\"] = \"EditingWebFormUnderPattern\"", probe);
            Assert.Contains("[\"severity\"] = \"warning\"", probe);
            Assert.Contains("[\"patternInstance\"] = resolved.Name", probe);
        }

        [Fact]
        public void TheWarningNamesTheToolCallThatAvoidsTheOverwrite()
        {
            // The advice both paths now give: the point is to edit the
            // PatternInstance part, and saying so without naming the tool leaves the
            // agent to guess the call.
            string message = WarningMessageSource();

            Assert.Contains("genexus_edit", message);
            Assert.Contains("part=PatternInstance", message);
            Assert.Contains("resolved.Name", message);
        }

        [Fact]
        public void TheWarningSaysWhichPartIsAtRiskAndWhatOverwritesIt()
        {
            string message = WarningMessageSource();

            Assert.Contains("partName", message);                                  // the part being edited
            Assert.Contains("WorkWithPlus PatternInstance", message);               // what covers it
            Assert.Contains("overwritten on the next pattern apply/save", message);
            Assert.Contains("SDPlus_Editor_Apply_On_Save", message);                // the escape hatch
        }

        [Fact]
        public void TheWarningNamesTheObjectTwiceRatherThanRepeatingTheLiteral()
        {
            // Three separate references: the parenthetical, the genexus_edit call,
            // and the toggle. Writing the name once and reusing a local instead
            // would be a refactor, not a behaviour change - pinned so the count is
            // a decision rather than an accident.
            string message = WarningMessageSource();

            Assert.Equal(3, SourceAssert.Count(message, "resolved.Name"));
        }

        /// <summary>The braces-balanced text of the method whose declaration starts with
        /// <paramref name="declaration"/>.</summary>

    }
}
