using System;
using System.Linq;
using System.Reflection;
using GxMcp.TestSupport;
using GxMcp.Worker.Compatibility;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A probe that omits <c>FlattenHierarchy</c> silently reports an SDK capability
    /// as unavailable, which is the failure <c>SdkMemberProbe</c> exists to prevent.
    /// Its binding-flag constants are therefore load-bearing, and the WebForm save
    /// diagnostics were spelling one of them out at six call sites instead of
    /// naming it - six chances to drop a flag.
    ///
    /// The new constant is <c>InstanceAnyVisibility</c>, and what matters about it
    /// is not its value but how it differs from the existing
    /// <c>StaticOrInstanceAnyVisibility</c>: it excludes <c>Static</c> on purpose.
    /// These probes invoke a member on a part <em>instance</em>, and
    /// <c>GetMethods</c> returning a same-named static overload first would let
    /// the probe report success for a call that did not do what the caller meant.
    /// That distinction is asserted here behaviourally, against a purpose-built
    /// type, rather than by comparing flag values - a test that only compared the
    /// constants would still pass if both were wrong in the same way.
    /// </summary>
    public class SdkMemberProbeBindingFlagTests
    {
        private class BaseWithHiddenInstance
        {
            internal void InheritedInstance() { }
        }

        private class ProbeTarget : BaseWithHiddenInstance
        {
            public void PublicInstance() { }
            private void PrivateInstance() { }
            public static void PublicStatic() { }
            private static void PrivateStatic() { }
        }

        private static string[] Found(BindingFlags flags) =>
            typeof(ProbeTarget).GetMethods(flags)
                .Where(m => m.DeclaringType != typeof(object))
                .Select(m => m.Name)
                .ToArray();

        [Fact]
        public void TheInstanceFlagSetFindsInheritedAndNonPublicInstanceMembers()
        {
            // The two things the constant is for. Inherited: the SDK moves helper
            // members between a type and its base class between GeneXus majors, and
            // without the hierarchy flag the probe reports the capability missing.
            // Non-public: C# emits explicitly implemented interface members as
            // private final methods, which Public alone cannot see.
            string[] found = Found(SdkMemberProbe.InstanceAnyVisibility);

            Assert.Contains("PublicInstance", found);
            Assert.Contains("PrivateInstance", found);
            Assert.Contains("InheritedInstance", found);
        }

        [Fact]
        public void TheInstanceFlagSetDoesNotSeeStaticMembers()
        {
            // This is the whole reason it is a separate constant rather than a
            // reuse of StaticOrInstanceAnyVisibility.
            string[] found = Found(SdkMemberProbe.InstanceAnyVisibility);

            Assert.DoesNotContain("PublicStatic", found);
            Assert.DoesNotContain("PrivateStatic", found);
        }

        [Fact]
        public void TheTwoConstantsDifferExactlyByTheStaticFlag()
        {
            // Pinned as a difference rather than as two literals, so adding a flag
            // to one of them is a deliberate act. A probe that wanted statics and
            // reached for the instance set would silently stop finding them.
            Assert.True(
                (SdkMemberProbe.StaticOrInstanceAnyVisibility & ~SdkMemberProbe.InstanceAnyVisibility) == BindingFlags.Static,
                "the two flag sets no longer differ by exactly Static");
        }

        [Fact]
        public void TheInstanceFlagSetStillCarriesTheHierarchyFlag()
        {
            // Guards the reason the constant exists, independent of the target type
            // above: if this flag is ever dropped, every inherited SDK helper stops
            // being found and the failure is a capability reported as unavailable.
            Assert.True((SdkMemberProbe.InstanceAnyVisibility & BindingFlags.FlattenHierarchy) != 0,
                "FlattenHierarchy was dropped from InstanceAnyVisibility");
            Assert.True((SdkMemberProbe.InstanceAnyVisibility & BindingFlags.NonPublic) != 0,
                "NonPublic was dropped from InstanceAnyVisibility");
            Assert.True((SdkMemberProbe.InstanceAnyVisibility & BindingFlags.Instance) != 0,
                "Instance was dropped from InstanceAnyVisibility");
        }

        [Fact]
        public void TheWebFormDiagnosticsNameTheFlagSetInsteadOfRewritingIt()
        {
            string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "WebFormSaveDiagnostics.cs"));

            // No method probe spells the flags out any more.
            Assert.Equal(0, SourceAssert.Count(source,
                "GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)"));

            // The eight GetProperty sites deliberately keep their own expression:
            // a property lookup is a different operation and does not flatten.
            // Asserting they are gone would push them onto a flag set that changes
            // which property is found, so the boundary is stated instead.
            Assert.Equal(8, SourceAssert.Count(source,
                "BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance"));
        }

        [Fact]
        public void EveryExactSignatureProbeGoesThroughTheSharedResolver()
        {
            string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Helpers", "WebFormSaveDiagnostics.cs"));

            // Three exact-signature probes, all previously hand-written predicates:
            // the three-argument LoadModelEntityOutput (which was written out
            // twice, in two diagnostics) and the distinct four-argument
            // LoadVersionIndependentOutput. They all name their parameter types,
            // which is what the resolver takes, so all three use it - leaving one
            // hand-written beside two converted is the inconsistency worth removing.
            Assert.Equal(0, SourceAssert.Count(source, "m.Name != \"LoadModelEntityOutput\""));
            Assert.Equal(0, SourceAssert.Count(source, "m.Name != \"LoadVersionIndependentOutput\""));
            Assert.Equal(3, SourceAssert.Count(source, "SdkMemberProbe.Resolve("));

            // The shared three-argument probe, at both of its former sites.
            Assert.Equal(2, SourceAssert.Count(source,
                "new[] { typeof(int), typeof(int), typeof(byte[]).MakeByRefType() }"));

            // The lookups that remain hand-written select by arity or by parameter
            // name - SaveHeader(), SaveHeader(SavePreferences) and
            // Save(<preferences>) - because their parameter type is discovered at
            // runtime rather than known, so a typed resolver cannot express them.
            // Counted on the flag-set call alone so the assertion does not depend
            // on this repository's line endings, and without the predicate, which
            // also belongs to an unrelated no-flags lookup elsewhere in the file.
            Assert.Equal(3, SourceAssert.Count(source, "GetMethods(SdkMemberProbe.InstanceAnyVisibility)"));
        }

    }
}
