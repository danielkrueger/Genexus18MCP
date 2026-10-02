using System;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.CustomTypes;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A WebPanel's MasterPage is a WebPanelReference. It used to be read as the CLR type name
    /// (ToString of the reference) and any name written to it was rejected up front by the
    /// scalar converter. Reads render the referenced object's name; writes accept a name.
    /// </summary>
    public class PropertyWebPanelReferenceTests
    {
        public class FakeReference
        {
            private readonly string _name;
            public FakeReference(string name) { _name = name; }
            public string GetName(KBModel model) => _name;
            public override string ToString() => "Fake.Namespace.FakeReference";
        }

        public class FakeDefinition { public Type Type { get; set; } public bool ReadOnly { get; set; } }

        public class FakeProperty
        {
            public string Name { get; set; }
            public FakeDefinition Definition { get; set; }
            public object Value { get; set; }
        }

        public class FakeContainer
        {
            public List<FakeProperty> Properties { get; } = new List<FakeProperty>();
        }

        [Fact]
        public void Render_ReferenceExposingGetName_YieldsReferencedName()
        {
            Assert.Equal("SampleMasterPage", PropertyService.RenderPropertyValue(new FakeReference("SampleMasterPage"), null));
        }

        [Fact]
        public void Render_EmptyReference_YieldsEmptyString()
        {
            Assert.Equal("", PropertyService.RenderPropertyValue(new FakeReference(null), null));
        }

        [Fact]
        public void Render_NoneRef_YieldsEmptyString_NotTheTypeName()
        {
            Assert.Equal("", PropertyService.RenderPropertyValue(WebPanelReference.NoneRef, null));
        }

        [Fact]
        public void Render_OtherValues_KeepToString()
        {
            Assert.Equal("5", PropertyService.RenderPropertyValue(5, null));
            Assert.Equal("abc", PropertyService.RenderPropertyValue("abc", null));
            Assert.Equal("", PropertyService.RenderPropertyValue(null, null));
        }

        [Fact]
        public void IsWebPanelReferenceType_OnlyForReferenceType()
        {
            Assert.True(PropertyService.IsWebPanelReferenceType(typeof(WebPanelReference)));
            Assert.False(PropertyService.IsWebPanelReferenceType(typeof(string)));
            Assert.False(PropertyService.IsWebPanelReferenceType(null));
        }

        [Fact]
        public void ValidatePropertyWrite_AcceptsAnObjectNameForAWebPanelReference()
        {
            var container = new FakeContainer();
            container.Properties.Add(new FakeProperty
            {
                Name = "MasterPage",
                Definition = new FakeDefinition { Type = typeof(WebPanelReference) },
                Value = WebPanelReference.NoneRef
            });

            Assert.Null(PropertyService.ValidatePropertyWrite(container, "MasterPage", "SampleMasterPage"));
            Assert.Null(PropertyService.ValidatePropertyWrite(container, "MasterPage", ""));
        }

        [Fact]
        public void ValidatePropertyWrite_StillRejectsBadScalarForOtherTypes()
        {
            var container = new FakeContainer();
            container.Properties.Add(new FakeProperty
            {
                Name = "Count",
                Definition = new FakeDefinition { Type = typeof(int) },
                Value = 1
            });

            Assert.StartsWith("InvalidPropertyValue", PropertyService.ValidatePropertyWrite(container, "Count", "abc"));
        }
    }
}
