using System;
using System.Xml;
using GxMcp.Worker.Compatibility;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// W1 step 2: <see cref="WebTagFactoryAdapter"/> probes the SDK's control-creation
    /// member structurally (name + argument-compatible 4-parameter shape), so the same
    /// path serves the real GX18 <c>WebTagFactory.Create</c> and every fake below.
    /// Follows the <c>SdkDeletionAdapter</c> contract: absent reports false, an SDK
    /// rejection propagates instead of degrading into a false "unsupported".
    /// </summary>
    public class WebTagFactoryAdapterTests
    {
        private sealed class ExactStaticShape
        {
            public static object Create(XmlNode node, object kb, object parent, bool ro)
                => "tag:" + ((XmlElement)node).Name + ":" + ro;
        }

        private sealed class NoCreate
        {
            public static object CreateNull() => null;
        }

        private sealed class WrongArity
        {
            public static object Create(XmlNode node, object kb) => null;
        }

        private sealed class ThrowingCreate
        {
            public static object Create(XmlNode node, object kb, object parent, bool ro)
                => throw new InvalidOperationException("sdk says no");
        }

        private sealed class InstanceOnly
        {
            public object Create(XmlNode node, object kb, object parent, bool ro) => "instance-tag";
        }

        private sealed class InstanceWithoutDefaultCtor
        {
            private InstanceWithoutDefaultCtor() { }
            public object Create(XmlNode node, object kb, object parent, bool ro) => "unreachable";
        }

        private sealed class Overloads
        {
            public static object Create(string a, string b, string c, bool ro) => "strings";
            public static object Create(XmlNode node, object kb, object parent, bool ro) => "nodes";
        }

        private sealed class AmbiguousOverloads
        {
            public static object Create(object a, object kb, object parent, bool ro) => "first";
            public static object Create(XmlNode node, object kb, object parent, bool ro) => "second";
        }

        private static XmlNode SomeNode()
        {
            var doc = new XmlDocument();
            return doc.CreateElement("gxTextBlock");
        }

        [Fact]
        public void TryCreateTagOn_PresentStaticShape_ReturnsTag()
        {
            bool ok = WebTagFactoryAdapter.TryCreateTagOn(
                typeof(ExactStaticShape), SomeNode(), new object(), null, out object tag);

            Assert.True(ok);
            // The trailing flag is the SDK's read-only marker, and a control being
            // created is never read-only. Pinning it keeps the removed parameter from
            // creeping back as a caller-supplied value.
            Assert.Equal("tag:gxTextBlock:False", tag);
        }

        [Fact]
        public void TryCreateTagOn_AbsentMember_ReturnsFalse()
        {
            bool ok = WebTagFactoryAdapter.TryCreateTagOn(
                typeof(NoCreate), SomeNode(), new object(), null, out object tag);

            Assert.False(ok);
            Assert.Null(tag);
        }

        [Fact]
        public void TryCreateTagOn_WrongSignature_ReturnsFalse()
        {
            bool ok = WebTagFactoryAdapter.TryCreateTagOn(
                typeof(WrongArity), SomeNode(), new object(), null, out object tag);

            Assert.False(ok);
            Assert.Null(tag);
        }

        [Fact]
        public void TryCreateTagOn_ThrowingCreate_PropagatesInsteadOfReportingUnsupported()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                WebTagFactoryAdapter.TryCreateTagOn(
                    typeof(ThrowingCreate), SomeNode(), new object(), null, out _));

            Assert.Contains("sdk says no", ex.Message);
        }

        [Fact]
        public void TryCreateTagOn_NullInputs_ReturnsFalse()
        {
            Assert.False(WebTagFactoryAdapter.TryCreateTagOn(null, SomeNode(), new object(), null, out _));
            Assert.False(WebTagFactoryAdapter.TryCreateTagOn(typeof(ExactStaticShape), null, new object(), null, out _));
        }

        [Fact]
        public void TryCreateTagOn_InstanceShape_InvokesViaDefaultConstructor()
        {
            bool ok = WebTagFactoryAdapter.TryCreateTagOn(
                typeof(InstanceOnly), SomeNode(), new object(), null, out object tag);

            Assert.True(ok);
            Assert.Equal("instance-tag", tag);
        }

        [Fact]
        public void TryCreateTagOn_InstanceWithoutDefaultConstructor_ReturnsFalse()
        {
            bool ok = WebTagFactoryAdapter.TryCreateTagOn(
                typeof(InstanceWithoutDefaultCtor), SomeNode(), new object(), null, out object tag);

            Assert.False(ok);
            Assert.Null(tag);
        }

        [Fact]
        public void TryCreateTagOn_Overloads_PrefersArgumentCompatibleShape()
        {
            bool ok = WebTagFactoryAdapter.TryCreateTagOn(
                typeof(Overloads), SomeNode(), "k", null, out object tag);

            Assert.True(ok);
            Assert.Equal("nodes", tag);
        }

        [Fact]
        public void TryCreateTagOn_EquallyCompatibleOverloads_FailsClosed()
        {
            // Both overloads accept the args with the same score: binding either
            // one arbitrarily could project the wrong control. Fail closed to the
            // raw path instead (same rule as AmbiguousMatchException handling).
            bool ok = WebTagFactoryAdapter.TryCreateTagOn(
                typeof(AmbiguousOverloads), SomeNode(), "k", null, out object tag);

            Assert.False(ok);
            Assert.Null(tag);
        }
    }
}
