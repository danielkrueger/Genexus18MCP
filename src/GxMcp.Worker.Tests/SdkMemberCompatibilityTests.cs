using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Artech.Architecture.Common.Parts;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Covers the compatibility probes that decide whether a GeneXus major exposes
    /// an SDK member. These guards are the whole payload of the v3.9.1 GX18
    /// compatibility fix, and the branches they exist for were previously
    /// unreachable from any test compiled against the primary SDK.
    /// </summary>
    public class SdkMemberCompatibilityTests
    {
        // ---- fixtures -------------------------------------------------------

        private sealed class HasBothMembers
        {
            public bool IsBuiltInModule(string name) => true;
            public string GetBuiltinModuleVersion(string name) => "1.2.3";
        }

        /// <summary>
        /// Stands in for a GeneXus 16 service: neither probed member exists. Measured
        /// against the real SDK, IModuleManagerService on GeneXus 16 exposes 17
        /// members and none of these two.
        /// </summary>
        private sealed class GeneXus16Shaped
        {
            public void UnrelatedMember() { }
        }

        /// <summary>Stands in for a GeneXus 17 service: no GetBuiltinModuleVersion.</summary>
        private sealed class GeneXus17Shaped
        {
            public bool IsBuiltInModule(string name) => true;
        }

        /// <summary>A member whose signature no longer matches the probed shape.</summary>
        private sealed class WrongSignatureShaped
        {
            public string IsBuiltInModule(int notAString) => "nope";
            public bool GetBuiltinModuleVersion(int notAString) => true;
        }

        private sealed class ThrowingProbe
        {
            public bool IsBuiltInModule(string name) => throw new InvalidOperationException("sdk refused");
            public string GetBuiltinModuleVersion(string name) => throw new InvalidOperationException("sdk refused");
        }

        private class StaticOnBase
        {
            public static bool InheritedStatic() => true;
        }

        private sealed class DerivedFromStaticOnBase : StaticOnBase
        {
            public bool InheritedInstance() => true;
        }

        // ---- Finding 1: the built-in module guards fail closed ---------------

        [Fact]
        public void TryIsBuiltInModuleOn_ReadsTheAnswerWhenTheMemberExists()
        {
            var target = new HasBothMembers();
            Assert.True(ModuleService.TryIsBuiltInModuleOn(typeof(HasBothMembers), target, "GeneXusGAM", out bool registered));
            Assert.True(registered);
        }

        [Fact]
        public void TryIsBuiltInModuleOn_FailsClosedWhenTheMemberIsAbsent_GeneXus16()
        {
            // This is the branch that reports ModuleBuiltinCheckUnsupported. It can
            // only be observed when the probed type genuinely lacks the member, which
            // is why the probed type is a parameter rather than derived internally.
            var target = new GeneXus16Shaped();
            Assert.False(ModuleService.TryIsBuiltInModuleOn(typeof(GeneXus16Shaped), target, "GeneXusGAM", out bool registered));
            Assert.False(registered);
        }

        [Fact]
        public void TryGetBuiltinModuleVersionOn_FailsClosedWhenTheMemberIsAbsent_GeneXus16And17()
        {
            // The branch that reports ModuleBuiltinVersionUnsupported: absent on 16
            // (no members at all) and on 17 (registration check only).
            Assert.False(ModuleService.TryGetBuiltinModuleVersionOn(
                typeof(GeneXus16Shaped), new GeneXus16Shaped(), "GeneXusGAM", out string v16));
            Assert.Null(v16);

            var seventeen = new GeneXus17Shaped();
            Assert.False(ModuleService.TryGetBuiltinModuleVersionOn(
                typeof(GeneXus17Shaped), seventeen, "GeneXusGAM", out string v17));
            Assert.Null(v17);
        }

        [Fact]
        public void TryGetBuiltinModuleVersionOn_ReadsTheVersionOnGeneXus18()
        {
            var target = new HasBothMembers();
            Assert.True(ModuleService.TryGetBuiltinModuleVersionOn(typeof(HasBothMembers), target, "GeneXusGAM", out string version));
            Assert.Equal("1.2.3", version);
        }

        [Fact]
        public void BuiltInProbes_RejectAWrongParameterOrReturnType()
        {
            // A major that changes the shape must degrade to "unverifiable", never to
            // a silently wrong answer.
            Assert.False(ModuleService.TryIsBuiltInModuleOn(
                typeof(WrongSignatureShaped), new WrongSignatureShaped(), "GeneXusGAM", out _));
            Assert.False(ModuleService.TryGetBuiltinModuleVersionOn(
                typeof(WrongSignatureShaped), new WrongSignatureShaped(), "GeneXusGAM", out _));
        }

        [Fact]
        public void BuiltInProbes_TreatAnSdkThrowAsUnverifiable()
        {
            // Fail closed: an SDK that throws while answering is not an SDK that
            // answered "no". Returning true here would install an unverified module.
            var throwing = new ThrowingProbe();
            Assert.False(ModuleService.TryIsBuiltInModuleOn(typeof(ThrowingProbe), throwing, "GeneXusGAM", out bool registered));
            Assert.False(registered);
            Assert.False(ModuleService.TryGetBuiltinModuleVersionOn(typeof(ThrowingProbe), throwing, "GeneXusGAM", out string version));
            Assert.Null(version);
        }

        [Fact]
        public void BuiltInProbes_RejectNulls()
        {
            Assert.False(ModuleService.TryIsBuiltInModule(null, "GeneXusGAM", out _));
            Assert.False(ModuleService.TryGetBuiltinModuleVersion(null, "GeneXusGAM", out _));
            Assert.False(ModuleService.TryIsBuiltInModuleOn(null, new HasBothMembers(), "GeneXusGAM", out _));
            Assert.False(ModuleService.TryIsBuiltInModuleOn(typeof(HasBothMembers), null, "GeneXusGAM", out _));
            Assert.False(ModuleService.TryGetBuiltinModuleVersionOn(typeof(HasBothMembers), null, "GeneXusGAM", out _));
        }

        // ---- Finding 3: hierarchy-safe member lookup ------------------------

        [Fact]
        public void StaticProbe_FindsAMemberDeclaredOnABaseType()
        {
            // The asymmetry this guards: a static member on a base type is invisible
            // to GetMethod without FlattenHierarchy, so a major that hoists a helper
            // member up the hierarchy would silently lose the capability.
            var derived = typeof(DerivedFromStaticOnBase);
            Assert.Null(derived.GetMethod("InheritedStatic", BindingFlags.Public | BindingFlags.Static));
            Assert.NotNull(SdkMemberProbe.Resolve(derived, "InheritedStatic", SdkMemberProbe.Static, Type.EmptyTypes));
        }

        [Fact]
        public void InstanceProbe_StillFindsInheritedMembers()
        {
            // FlattenHierarchy is inert for instance members, so adding it cannot
            // change instance resolution - it only fixes the static case.
            var derived = typeof(DerivedFromStaticOnBase);
            Assert.NotNull(derived.GetMethod("InheritedInstance", BindingFlags.Public | BindingFlags.Instance));
            Assert.NotNull(SdkMemberProbe.Resolve(derived, "InheritedInstance", SdkMemberProbe.Instance, Type.EmptyTypes));
        }

        [Fact]
        public void Resolve_ReturnsNullForAbsentMembersAndBadArguments()
        {
            Assert.Null(SdkMemberProbe.Resolve(typeof(HasBothMembers), "NoSuchMember", SdkMemberProbe.Instance, Type.EmptyTypes));
            Assert.Null(SdkMemberProbe.Resolve(null, "IsBuiltInModule", SdkMemberProbe.Instance, Type.EmptyTypes));
            Assert.Null(SdkMemberProbe.Resolve(typeof(HasBothMembers), "  ", SdkMemberProbe.Instance, Type.EmptyTypes));
        }

        [Fact]
        public void Resolve_DoesNotThrowOnAnAmbiguousSignature()
        {
            // Ambiguous means the probe cannot bind, so it reports absent and the
            // caller degrades instead of picking a base class arbitrarily.
            var result = SdkMemberProbe.Resolve(typeof(DerivedFromStaticOnBase),
                "InheritedStatic", SdkMemberProbe.Static, Type.EmptyTypes);
            Assert.NotNull(result);
        }

        // ---- the cross-major dependency source used by the module preview -----

        [Fact]
        public void TryGetExportDependenciesOn_ReturnsNullForAnythingItCannotRead()
        {
            // The preview must be able to say "never asked" instead of reporting an
            // empty dependency list as authoritative, so every unreadable shape
            // returns null rather than an empty sequence.
            Assert.Null(ModuleService.TryGetExportDependencies(null));
            Assert.Null(ModuleService.TryGetExportDependencies(new object()));
            Assert.Null(ModuleService.TryGetExportDependencies("not a module"));
        }

        [Fact]
        public void TryGetExportDependenciesOn_NeverThrowsOnAThrowingModule()
        {
            // A hostile or half-initialized SDK object must not be able to fail a
            // read-only preview; the call is an enrichment, not a gate.
            var outcome = ModuleService.TryGetExportDependencies(new ExplodingModule());
            Assert.Null(outcome);
        }

        private sealed class ExplodingModule
        {
            public ModuleContentPart Parts => throw new InvalidOperationException("sdk refused");
        }

        [Fact]
        public void SelectDependencySource_PrefersTheCrossMajorPartOverTheGeneXus18OnlyService()
        {
            // The part is present on 16/17/18; the service member is 18-only. Reading
            // the service member first reported "unavailable" on the older majors even
            // though a working read was available, so the preference is asserted here
            // as behaviour rather than as source text.
            var part = new[] { "from-part" };
            var service = new[] { "from-service" };

            var chosen = ModuleService.SelectDependencySource(part, service, out string source);
            Assert.Equal("modulePart", source);
            Assert.Same(part, chosen);
        }

        [Fact]
        public void SelectDependencySource_FallsBackToTheServiceWhenThePartIsAbsent()
        {
            var service = new[] { "from-service" };
            var chosen = ModuleService.SelectDependencySource(null, service, out string source);
            Assert.Equal("sdk", source);
            Assert.Same(service, chosen);
        }

        [Fact]
        public void SelectDependencySource_ReportsUnavailableRatherThanAnEmptyList()
        {
            // Both sources unreadable must yield null plus "unavailable", never an
            // empty sequence, so the caller cannot present "never asked" as
            // "needs nothing".
            var chosen = ModuleService.SelectDependencySource(null, null, out string source);
            Assert.Null(chosen);
            Assert.Equal("unavailable", source);
        }

        [Fact]
        public void SelectDependencySource_TreatsAnEmptyButReadablePartAsAuthoritative()
        {
            // A module that genuinely has no dependencies is different from a module
            // whose dependencies could not be read; the empty list must survive as
            // "modulePart" so the caller can tell them apart.
            var emptyPart = new string[0];
            var chosen = ModuleService.SelectDependencySource(emptyPart, new[] { "stale" }, out string source);
            Assert.Equal("modulePart", source);
            Assert.NotNull(chosen);
            Assert.Empty(chosen);
        }

        // ---- Finding 2: the deletion adapter's candidate order --------------

        private sealed class ModernDeleteTarget
        {
            public bool Called;
            public void Delete() => Called = true;
            public void Remove() => Called = false;
        }

        private sealed class LegacyRemoveTarget
        {
            public bool Called;
            public void Remove() => Called = true;
        }

        private sealed class NoDeleteMember
        {
        }

        private sealed class ThrowingDeleteTarget
        {
            public void Delete() => throw new InvalidOperationException("sdk refused the delete");
        }

        [Fact]
        public void TryDeleteOrRemove_PrefersDeleteAndNeverFallsBackAfterIt()
        {
            var modern = new ModernDeleteTarget();
            Assert.True(SdkDeletionAdapter.TryDeleteOrRemove(modern));
            Assert.True(modern.Called);
        }

        [Fact]
        public void TryDeleteOrRemove_FallsBackToRemoveOnALegacyMajor()
        {
            var legacy = new LegacyRemoveTarget();
            Assert.True(SdkDeletionAdapter.TryDeleteOrRemove(legacy));
            Assert.True(legacy.Called);
        }

        [Fact]
        public void TryDeleteOrRemove_ReportsAbsentWhenNeitherMemberExists()
        {
            Assert.False(SdkDeletionAdapter.TryDeleteOrRemove(new NoDeleteMember()));
        }

        [Fact]
        public void TryDeleteOrRemove_PropagatesAnSdkRejectionInsteadOfSwallowingIt()
        {
            // Delete exists but the SDK refuses: that is a real failure the caller
            // must see, not an "unsupported major" false.
            var ex = Assert.Throws<InvalidOperationException>(
                () => SdkDeletionAdapter.TryDeleteOrRemove(new ThrowingDeleteTarget()));
            Assert.Contains("sdk refused the delete", ex.Message);
        }

        [Fact]
        public void TryDeleteOrRemove_RejectsNull()
        {
            Assert.False(SdkDeletionAdapter.TryDeleteOrRemove(null));
        }
    }
}
