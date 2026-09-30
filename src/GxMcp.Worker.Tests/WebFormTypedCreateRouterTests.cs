using System;
using System.Collections.Generic;
using System.Xml;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WebFormTypedCreateRouterTests
    {
        private static WebFormStructuralChange Added(string type, string name)
        {
            return new WebFormStructuralChange { Kind = WebFormStructuralChangeKind.Added, ControlType = type, ControlName = name, Path = "/GxMultiForm/Form/body" };
        }

        private static XmlDocument Doc(string xml)
        {
            var document = new XmlDocument();
            document.LoadXml(xml);
            return document;
        }

        [Fact]
        public void ShouldAttemptTypedCreate_AddControlsOnly_ReturnsTrue()
        {
            var changes = new List<WebFormStructuralChange>
            {
                Added("gxTextBlock", "T1"),
                Added("gxButton", "B1")
            };

            Assert.True(WebFormTypedCreateRouter.ShouldAttemptTypedCreate(changes));
        }

        [Fact]
        public void ShouldAttemptTypedCreate_MixedStructureAndControlAdds_ReturnsTrue()
        {
            // The realistic authoring shape: wrapper structure plus the control. The
            // structure persists through the raw rewrite that always runs afterwards;
            // the attempt materializes the identified control.
            var changes = new List<WebFormStructuralChange>
            {
                new WebFormStructuralChange { Kind = WebFormStructuralChangeKind.Added, ControlType = "row", Path = "/GxMultiForm/Form/body" },
                new WebFormStructuralChange { Kind = WebFormStructuralChangeKind.Added, ControlType = "cell", Path = "/GxMultiForm/Form/body/row" },
                Added("gxTextBlock", "T1")
            };

            Assert.True(WebFormTypedCreateRouter.ShouldAttemptTypedCreate(changes));
        }

        [Fact]
        public void ShouldAttemptTypedCreate_StructureOnlyAdds_ReturnsFalse()
        {
            var changes = new List<WebFormStructuralChange>
            {
                new WebFormStructuralChange { Kind = WebFormStructuralChangeKind.Added, ControlType = "row", Path = "/x" },
                new WebFormStructuralChange { Kind = WebFormStructuralChangeKind.Added, ControlType = "cell", Path = "/x/row" }
            };

            Assert.False(WebFormTypedCreateRouter.ShouldAttemptTypedCreate(changes));
        }

        [Theory]
        [InlineData(WebFormStructuralChangeKind.Added, "gxTextBlock", "T1", true)]
        [InlineData(WebFormStructuralChangeKind.Added, "gxButton", "B1", true)]
        [InlineData(WebFormStructuralChangeKind.Added, "row", null, false)]
        [InlineData(WebFormStructuralChangeKind.Added, "cell", "", false)]
        [InlineData(WebFormStructuralChangeKind.Added, "table", "MainTable", false)]
        [InlineData(WebFormStructuralChangeKind.Added, "gxButton", null, false)]
        [InlineData(WebFormStructuralChangeKind.Removed, "gxButton", "B1", false)]
        [InlineData(WebFormStructuralChangeKind.Moved, "gxTextBlock", "T1", false)]
        public void IsControlAdd_ClassifiesOnlyIdentifiedAddedControls(
            WebFormStructuralChangeKind kind, string controlType, string name, bool expected)
        {
            var change = new WebFormStructuralChange { Kind = kind, ControlType = controlType, ControlName = name };

            Assert.Equal(expected, WebFormTypedCreateRouter.IsControlAdd(change));
            Assert.False(WebFormTypedCreateRouter.IsControlAdd(null));
        }

        [Theory]
        [InlineData("table", "MainTable")]
        [InlineData("row", null)]
        [InlineData("cell", "")]
        public void ShouldAttemptTypedCreate_StructureAdd_ReturnsFalse(string type, string name)
        {
            Assert.False(WebFormTypedCreateRouter.ShouldAttemptTypedCreate(
                new List<WebFormStructuralChange> { Added(type, name) }));
        }

        [Fact]
        public void ShouldAttemptTypedCreate_RemovedOrMoved_ReturnsFalse()
        {
            Assert.False(WebFormTypedCreateRouter.ShouldAttemptTypedCreate(
                new List<WebFormStructuralChange>
                {
                    new WebFormStructuralChange { Kind = WebFormStructuralChangeKind.Removed, ControlType = "gxButton", ControlName = "B1" }
                }));
            Assert.False(WebFormTypedCreateRouter.ShouldAttemptTypedCreate(
                new List<WebFormStructuralChange>
                {
                    new WebFormStructuralChange { Kind = WebFormStructuralChangeKind.Moved, ControlType = "gxButton", ControlName = "B1" }
                }));
            Assert.False(WebFormTypedCreateRouter.ShouldAttemptTypedCreate(
                new List<WebFormStructuralChange>()));
            Assert.False(WebFormTypedCreateRouter.ShouldAttemptTypedCreate(null));
        }

        [Fact]
        public void ShouldAttemptTypedCreate_AddedControlWithoutIdentity_ReturnsFalse()
        {
            Assert.False(WebFormTypedCreateRouter.ShouldAttemptTypedCreate(
                new List<WebFormStructuralChange> { Added("gxButton", null) }));
        }

        [Fact]
        public void FindAddedElement_LocatesByControlName()
        {
            var document = Doc("<GxMultiForm><Form><body><gxButton ControlName=\"B1\" Caption=\"Go\" /></body></Form></GxMultiForm>");

            XmlElement element = WebFormTypedCreateRouter.FindAddedElement(document, Added("gxButton", "B1"));

            Assert.NotNull(element);
            Assert.Equal("B1", element.GetAttribute("ControlName"));
            Assert.Equal("body", ((XmlElement)element.ParentNode).LocalName);
        }

        [Fact]
        public void ImportInto_AttachesNodeToThePartDocument()
        {
            var partDoc = Doc("<GxMultiForm><Form><body><table ControlName=\"T\"><row><cell /></row></table></body></Form></GxMultiForm>");
            var requested = Doc("<GxMultiForm><Form><body><table ControlName=\"T\"><row><cell><gxTextBlock ControlName=\"W1\" Caption=\"W\" /></cell></row></table></body></Form></GxMultiForm>");
            XmlElement requestedText = WebFormTypedCreateRouter.FindAddedElement(requested, Added("gxTextBlock", "W1"));
            Assert.NotNull(requestedText);

            XmlElement imported = WebFormTypedCreateRouter.ImportInto(partDoc, requestedText, out string failure);

            Assert.Null(failure);
            Assert.NotNull(imported);
            Assert.Same(partDoc, imported.OwnerDocument);
            // The control is now a descendant of the part document, not only of the request.
            Assert.Equal(1, partDoc.GetElementsByTagName("gxTextBlock").Count);
        }

        [Fact]
        public void ImportInto_AssignsIdWhenTheRequestHasNone()
        {
            // The SDK addresses elements by id (EnumerateWebTag keys on it), and every
            // GeneXus-created element carries one. Without it the created tag wraps a
            // node the part can never resolve again.
            var partDoc = Doc("<GxMultiForm><Form /></GxMultiForm>");
            var requested = Doc("<GxMultiForm><Form><gxTextBlock ControlName=\"W2\" /></Form></GxMultiForm>");
            XmlElement requestedText = WebFormTypedCreateRouter.FindAddedElement(requested, Added("gxTextBlock", "W2"));

            XmlElement imported = WebFormTypedCreateRouter.ImportInto(partDoc, requestedText, out string failure);

            Assert.Null(failure);
            Assert.NotNull(imported);
            Assert.False(string.IsNullOrWhiteSpace(imported.GetAttribute("id")));
            Assert.True(Guid.TryParse(imported.GetAttribute("id"), out _));
        }

        [Fact]
        public void ImportInto_KeepsAnExplicitId()
        {
            var partDoc = Doc("<GxMultiForm><Form /></GxMultiForm>");
            var requested = Doc("<GxMultiForm><Form><gxTextBlock id=\"kept-id\" ControlName=\"W3\" /></Form></GxMultiForm>");
            XmlElement requestedText = WebFormTypedCreateRouter.FindAddedElement(requested, Added("gxTextBlock", "W3"));

            XmlElement imported = WebFormTypedCreateRouter.ImportInto(partDoc, requestedText, out _);

            Assert.Equal("kept-id", imported.GetAttribute("id"));
        }

        [Fact]
        public void ImportInto_UnknownParent_FallsBackToRoot()
        {
            var partDoc = Doc("<GxMultiForm><Form /></GxMultiForm>");
            var requested = Doc("<GxMultiForm><Form><gxTextBlock ControlName=\"W1\" /></Form></GxMultiForm>");
            XmlElement requestedText = WebFormTypedCreateRouter.FindAddedElement(requested, Added("gxTextBlock", "W1"));

            XmlElement imported = WebFormTypedCreateRouter.ImportInto(partDoc, requestedText, out string failure);

            Assert.Null(failure);
            Assert.NotNull(imported);
            Assert.Same(partDoc, imported.OwnerDocument);
        }

        [Fact]
        public void ImportInto_EmptyPartDocument_ReturnsNull()
        {
            var requested = Doc("<GxMultiForm><Form><gxTextBlock ControlName=\"W1\" /></Form></GxMultiForm>")
                .DocumentElement!.FirstChild! as XmlElement;

            Assert.Null(WebFormTypedCreateRouter.ImportInto(new XmlDocument(), requested, out string failure));
            Assert.NotNull(failure);
        }

        private sealed class TagWithSaveProperties
        {
            public int SavePropertiesCalls;
            public bool SaveProperties() { SavePropertiesCalls++; return true; }
        }

        private sealed class TagWithoutSaveProperties
        {
        }

        private sealed class TagWithFalseSaveProperties
        {
            public bool SaveProperties() => false;
        }

        private sealed class TagWithThrowingSaveProperties
        {
            public void SaveProperties() => throw new InvalidOperationException("nope");
        }

        [Fact]
        public void ReassertCreatedTags_CallsSavePropertiesOnEachTag()
        {
            var a = new TagWithSaveProperties();
            var b = new TagWithSaveProperties();

            WebFormTypedCreateRouter.ReassertCreatedTags(new object(), new object[] { a, b });

            Assert.Equal(1, a.SavePropertiesCalls);
            Assert.Equal(1, b.SavePropertiesCalls);
        }

        [Fact]
        public void ReassertCreatedTags_SwallowsARejectedSave()
        {
            // SaveProperties=false means the SDK refused the tag back; re-assert must
            // not throw, and the post-write verification remains the reporter.
            WebFormTypedCreateRouter.ReassertCreatedTags(new object(), new object[] { new TagWithFalseSaveProperties() });
        }

        [Fact]
        public void ReassertCreatedTags_IgnoresTagWithoutSaveProperties()
        {
            WebFormTypedCreateRouter.ReassertCreatedTags(new object(), new object[] { new TagWithoutSaveProperties() });
        }

        [Fact]
        public void ReassertCreatedTags_SwallowsSdkRejection()
        {
            // A rejected re-assert must not turn into a write failure of its own; the
            // caller's post-write verification is the gate.
            WebFormTypedCreateRouter.ReassertCreatedTags(new object(), new object[] { new TagWithThrowingSaveProperties() });
        }

        [Fact]
        public void ReassertCreatedTags_IgnoresNullsAndEmptyInput()
        {
            WebFormTypedCreateRouter.ReassertCreatedTags(null, new object[] { new TagWithSaveProperties() });
            WebFormTypedCreateRouter.ReassertCreatedTags(new object(), null);
            WebFormTypedCreateRouter.ReassertCreatedTags(new object(), new object[0]);
            WebFormTypedCreateRouter.ReassertCreatedTags(new object(), new object[] { null });
        }

        [Fact]
        public void Depth_OrdersParentsBeforeChildren()
        {
            var child = new WebFormStructuralChange { Kind = WebFormStructuralChangeKind.Added, ControlType = "gxTextBlock", ControlName = "W", Path = "/GxMultiForm/Form/detail/layout/table/row/cell" };
            var outer = new WebFormStructuralChange { Kind = WebFormStructuralChangeKind.Added, ControlType = "gxButton", ControlName = "B", Path = "/GxMultiForm/Form/detail" };

            Assert.True(WebFormTypedCreateRouter.OrderByDepth(new[] { child, outer })[0] == outer);
            Assert.True(WebFormTypedCreateRouter.OrderByDepth(new[] { outer, child })[0] == outer);
        }

        [Fact]
        public void FindAddedElement_MissingIdentity_ReturnsNull()
        {
            var document = Doc("<GxMultiForm><Form><body /></Form></GxMultiForm>");

            Assert.Null(WebFormTypedCreateRouter.FindAddedElement(document, Added("gxButton", "Nope")));
        }

        [Fact]
        public void ParentIdentity_PrefersControlNameBecauseIdsChurn()
        {
            // One identity order for both typed writers. ControlName is author-stable;
            // ids are regenerated when the SDK materializes a replaced subtree (measured
            // live). The tag index is keyed under both, so either resolves — but asking
            // for the churning one first is how a control ends up "not found".
            var document = Doc("<GxMultiForm><Form><cell id=\"c1\" ControlName=\"Cell\"><gxButton ControlName=\"B1\" /></cell></Form></GxMultiForm>");
            XmlElement button = WebFormTypedCreateRouter.FindAddedElement(document, Added("gxButton", "B1"));

            Assert.Equal("Cell", WebFormSdkReflection.GetControlIdentity(button.ParentNode as XmlElement));
        }

        [Fact]
        public void FindType_UnknownName_ReturnsNull()
        {
            Assert.Null(WebFormSdkReflection.FindType("No.Such.Type.Anywhere"));
            Assert.Null(WebFormSdkReflection.FindType(null));
        }

        private sealed class FakeFactoryShape
        {
            public static object Create(XmlNode node, object kb, object parent, bool ro) => "fake";
        }

        [Fact]
        public void FindType_LoadedAssemblyType_Resolves()
        {
            Assert.Same(typeof(FakeFactoryShape), WebFormSdkReflection.FindType(typeof(FakeFactoryShape).FullName));
        }
    }
}
