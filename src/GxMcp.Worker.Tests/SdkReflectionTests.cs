using System;
using System.Reflection;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The SDK-probe helpers existed as ~90 private copies whose bodies were nearly
    /// identical but not equivalent: they disagreed on visibility flags, on guarding
    /// indexer properties, on checking <c>CanWrite</c> before <c>SetValue</c>, and on
    /// what a null input means. These pin the one behaviour every copy now shares —
    /// and specifically the three places where "nearly identical" was a bug.
    /// </summary>
    public class SdkReflectionTests
    {
        // ------------------------------------------------------------- truncate

        [Fact]
        public void Truncate_NeverExceedsTheRequestedWidth()
        {
            // Six of the eight copies appended the ellipsis AFTER slicing to `max`,
            // producing `max + 1` characters; two sliced to `max - 1` and matched.
            // A caller sizing a buffer or comparing lengths got a different answer
            // depending on which service produced the string.
            for (int width = 1; width <= 8; width++)
            {
                string result = SdkReflection.Truncate(new string('x', 40), width);
                Assert.Equal(width, result.Length);
            }
        }

        [Fact]
        public void Truncate_LeavesShortValuesAndRejectsNonPositiveWidths()
        {
            Assert.Equal("short", SdkReflection.Truncate("short", 10));
            Assert.Equal("exact", SdkReflection.Truncate("exact", 5));
            Assert.Equal(string.Empty, SdkReflection.Truncate(null, 10));
            Assert.Equal(string.Empty, SdkReflection.Truncate(string.Empty, 10));
            Assert.Equal(string.Empty, SdkReflection.Truncate("value", 0));
            Assert.Equal(string.Empty, SdkReflection.Truncate("value", -1));
        }

        [Fact]
        public void Truncate_MarkstheCut()
        {
            Assert.EndsWith("…", SdkReflection.Truncate("abcdefghij", 4));
        }

        // ---------------------------------------------------------------- sha256

        [Fact]
        public void Sha256_PropagatesNull()
        {
            // One copy returned null for a null input; two hashed it as the empty
            // string. That made a restore check of the shape
            // Equals(Sha256(restored), Sha256(before)) compare null to null and
            // report "restored exactly" for a snapshot that was never restored.
            Assert.Null(SdkReflection.Sha256Hex(null));
        }

        [Fact]
        public void Sha256OrEmpty_FoldsNullIntoEmpty()
        {
            Assert.NotNull(SdkReflection.Sha256OrEmpty(null));
            Assert.Equal(SdkReflection.Sha256Hex(string.Empty), SdkReflection.Sha256OrEmpty(null));
        }

        [Fact]
        public void Sha256_IsStableAndDistinguishesValues()
        {
            Assert.Equal(SdkReflection.Sha256Hex("abc"), SdkReflection.Sha256Hex("abc"));
            Assert.NotEqual(SdkReflection.Sha256Hex("abc"), SdkReflection.Sha256Hex("abd"));
            Assert.Equal(64, SdkReflection.Sha256Hex("abc").Length);
        }

        // ------------------------------------------------------------ mark dirty

        private sealed class WritableDirty { public bool Dirty { get; set; } public bool IsDirty { get; set; } }
        private sealed class ReadOnlyDirty { public bool Dirty { get { return false; } } }
        private sealed class WrongTypeDirty { public int Dirty { get; set; } }
        private sealed class IsDirtyOnly { public bool IsDirty { get; set; } }
        private sealed class NoDirtyAtAll { public string Name { get; set; } }

        [Fact]
        public void MarkDirty_SetsEitherAlias()
        {
            var dirty = new WritableDirty();
            Assert.True(SdkReflection.MarkDirty(dirty));
            Assert.True(dirty.Dirty || dirty.IsDirty);
        }

        [Fact]
        public void MarkDirty_FallsBackToTheIsDirtyAlias()
        {
            var only = new IsDirtyOnly();
            Assert.True(SdkReflection.MarkDirty(only));
            Assert.True(only.IsDirty);
        }

        [Fact]
        public void MarkDirty_ReportsFalseInsteadOfThrowingOnAReadOnlyMember()
        {
            // The WriteService copy called SetValue with no CanWrite check, so a
            // read-only Dirty threw, the caller's outer catch swallowed it, and the
            // part silently stayed clean — a write that reported success and
            // persisted nothing.
            Assert.False(SdkReflection.MarkDirty(new ReadOnlyDirty()));
        }

        [Fact]
        public void MarkDirty_ReportsFalseInsteadOfThrowingOnAWrongType()
        {
            Assert.False(SdkReflection.MarkDirty(new WrongTypeDirty()));
        }

        [Fact]
        public void MarkDirty_ReportsFalseWhenThereIsNoDirtyMember()
        {
            Assert.False(SdkReflection.MarkDirty(new NoDirtyAtAll()));
            Assert.False(SdkReflection.MarkDirty(null));
        }

        // ------------------------------------------------------- safe invocation

        private sealed class ThrowingProbe { public object Boom() => throw new InvalidOperationException("sdk"); }
        private sealed class BoolProbe { public bool Flag() => true; public bool Off() => false; }

        [Fact]
        public void TryInvokeNoArgs_NeverPropagatesTheCalleesFailure()
        {
            // The KbStartupService copy was named Try*, had no try/catch, and let a
            // throwing GetPropertyValue escape. A Try helper that throws is worse than
            // none: the caller stops guarding the call.
            Assert.Null(SdkReflection.TryInvokeNoArgs(new ThrowingProbe(), "Boom"));
            Assert.Null(SdkReflection.TryInvokeNoArgs(new ThrowingProbe(), "Missing"));
            Assert.Null(SdkReflection.TryInvokeNoArgs(null, "Anything"));
        }

        [Fact]
        public void TryInvokeBool_CoercesAndDefaultsToFalse()
        {
            Assert.True(SdkReflection.TryInvokeBool(new BoolProbe(), "Flag"));
            Assert.False(SdkReflection.TryInvokeBool(new BoolProbe(), "Off"));
            Assert.False(SdkReflection.TryInvokeBool(new BoolProbe(), "Missing"));
            Assert.False(SdkReflection.TryInvokeBool(new ThrowingProbe(), "Boom"));
        }

        [Fact]
        public void TryInvokeNoArgs_WalksTheBaseChain()
        {
            // SDK members are frequently declared on a base class and hidden by a
            // derived declaration, so a declared-only lookup would miss them.
            Assert.NotNull(SdkReflection.TryInvokeNoArgs(new DerivedProbe(), "Inherited"));
        }

        private class BaseProbe
        {
            public object Inherited() => "from-base";
            public string InheritedProperty => "from-base";
        }
        private sealed class DerivedProbe : BaseProbe { }

        // ------------------------------------------------------------- finders

        [Fact]
        public void FindMethod_WalksTheBaseChain()
        {
            Assert.NotNull(SdkReflection.FindMethod(typeof(DerivedProbe), "Inherited"));
            Assert.Null(SdkReflection.FindMethod(typeof(DerivedProbe), "Missing"));
            Assert.Null(SdkReflection.FindMethod(null, "Inherited"));
        }

        [Fact]
        public void FindProperty_SkipsIndexers()
        {
            // GetValue on an indexer throws for a missing index, so a probe that
            // returns one is a probe that blows up on a perfectly ordinary SDK type.
            Assert.Null(SdkReflection.FindProperty(typeof(DerivedProbe), "this"));
            Assert.NotNull(SdkReflection.FindProperty(typeof(DerivedProbe), nameof(BaseProbe.InheritedProperty)));
        }

        // ------------------------------------------------------------- read/write

        [Fact]
        public void Read_RejectsIndexersAndNeverThrows()
        {
            Assert.Null(SdkReflection.Read(new ThrowingProbe(), "this"));
            Assert.Null(SdkReflection.Read(null, "Anything"));
            Assert.Equal("from-base", SdkReflection.Read(new DerivedProbe(), "InheritedProperty"));
        }

        [Fact]
        public void TrySet_RefusesAReadOnlyProperty()
        {
            Assert.False(SdkReflection.TrySet(new ReadOnlyProperty(), "Value", "x"));
        }

        [Fact]
        public void TrySet_WritesAWritableProperty()
        {
            var target = new WritableProperty();
            Assert.True(SdkReflection.TrySet(target, "Value", "x"));
            Assert.Equal("x", target.Value);
        }

        [Fact]
        public void TrySetBool_RefusesANonBoolean()
        {
            Assert.False(SdkReflection.TrySetBool(new WritableProperty(), "Value", true));
        }

        [Fact]
        public void ReadFirst_TriesEachAliasInOrder()
        {
            Assert.Equal("from-base", SdkReflection.ReadFirst(new DerivedProbe(), "Missing", "InheritedProperty"));
            Assert.Null(SdkReflection.ReadFirst(new DerivedProbe(), "Missing", "AlsoMissing"));
        }

        private sealed class WritableProperty { public string Value { get; set; } }
        private sealed class ReadOnlyProperty { public string Value { get { return "fixed"; } } }

        // ------------------------------------------------------------ safe probe

        [Fact]
        public void Safe_SwallowsAndReturnsTheFallback()
        {
            Assert.Equal(7, SdkReflection.Safe(() => 7));
            Assert.Equal(-1, SdkReflection.Safe<int>(() => throw new InvalidOperationException(), -1));
        }
    }
}
