using System;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Services.Structure;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #425: "Show in Default Forms" per Transaction attribute.
    public class ShowInDefaultFormsTests
    {
        private static Type Sdk() => typeof(Artech.Genexus.Common.Objects.Transaction).Assembly
            .GetType("Artech.Genexus.Common.Properties+TransactionAttribute");

        [Fact]
        public void SdkConstantAndHelperSignaturesArePinned()
        {
            Type sdk = Sdk();
            Assert.NotNull(sdk);
            Assert.Equal("IncludeInForms", sdk.GetField("ShowInDefaultForms").GetRawConstantValue());
            Assert.Equal("IncludeInForms", (string)typeof(TransactionAttributeFormsProperty)
                .GetField("PropertyKey", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public).GetRawConstantValue());

            MethodInfo get = sdk.GetMethod("GetShowInDefaultForms", BindingFlags.Public | BindingFlags.Static);
            MethodInfo set = sdk.GetMethod("SetShowInDefaultForms", BindingFlags.Public | BindingFlags.Static);
            Assert.Equal(typeof(bool), get.ReturnType);
            Assert.Single(get.GetParameters());
            Assert.Equal(new[] { "IPropertyBag", "Boolean" }, set.GetParameters().Select(p => p.ParameterType.Name));
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void BooleansAndBooleanStringsAreAccepted(string json, bool expected)
        {
            Assert.True(TransactionAttributeFormsProperty.TryParse(JToken.Parse(json), out bool value));
            Assert.Equal(expected, value);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("\"yes\"")]
        [InlineData("null")]
        public void NonBooleansAreRejected(string json)
            => Assert.False(TransactionAttributeFormsProperty.TryParse(JToken.Parse(json), out _));

        [Fact]
        public void FindInvalidReportsTheOffendingItemAtAnyDepthAndIgnoresAbsentValues()
        {
            var items = JArray.Parse(@"[{name:'A'},{name:'Lines',isLevel:true,children:[{name:'B',showInDefaultForms:false},{name:'C',showInDefaultForms:'maybe'}]}]");
            Assert.Equal("C", TransactionAttributeFormsProperty.FindInvalid(items));
            Assert.Null(TransactionAttributeFormsProperty.FindInvalid(JArray.Parse("[{name:'A',showInDefaultForms:true}]")));
        }
    }
}
