using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WebFormPropertyDeltaTests
    {
        [Fact]
        public void DetectSupportedPropertyDeltas_CapturesCaptionChangeOnExistingControl()
        {
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"TextBlockSaldoHoras\" Caption=\"Saldo\" Class=\"TextBlock\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"TextBlockSaldoHoras\" Caption=\"Saldo horas\" Class=\"TextBlock\" /></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.True(result.IsSupported);
            var delta = Assert.Single(result.Deltas);
            Assert.Equal("TextBlockSaldoHoras", delta.ControlName);
            Assert.Equal("Caption", delta.PropertyName);
            Assert.Equal("Saldo horas", delta.Value);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_IgnoresFormattingWhitespace()
        {
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"TextBlockSaldoHoras\" Caption=\"Saldo\" Class=\"TextBlock\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm>\r\n  <Form>\r\n    <body>\r\n      <gxTextBlock ControlName=\"TextBlockSaldoHoras\" Caption=\"Saldo horas\" Class=\"TextBlock\" />\r\n    </body>\r\n  </Form>\r\n</GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.True(result.IsSupported);
            Assert.Single(result.Deltas);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_RejectsStructuralChanges()
        {
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /><gxTextBlock ControlName=\"B\" Caption=\"B\" /></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.False(result.IsSupported);
            Assert.Empty(result.Deltas);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_ClassifiesAddedControl()
        {
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /><gxButton ControlName=\"Btn\" Caption=\"Go\" /></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.False(result.IsSupported);
            Assert.Empty(result.Deltas);
            var added = Assert.Single(result.StructuralChanges);
            Assert.Equal(WebFormStructuralChangeKind.Added, added.Kind);
            Assert.Equal("gxButton", added.ControlType);
            Assert.Equal("Btn", added.ControlName);
            Assert.Contains("body", added.Path);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_ClassifiesRemovedControl()
        {
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /><gxButton ControlName=\"Btn\" Caption=\"Go\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.False(result.IsSupported);
            Assert.Empty(result.Deltas);
            var removed = Assert.Single(result.StructuralChanges);
            Assert.Equal(WebFormStructuralChangeKind.Removed, removed.Kind);
            Assert.Equal("gxButton", removed.ControlType);
            Assert.Equal("Btn", removed.ControlName);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_ClassifiesMovedControl()
        {
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /><gxButton ControlName=\"Btn\" Caption=\"Go\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><gxButton ControlName=\"Btn\" Caption=\"Go\" /><gxTextBlock ControlName=\"A\" Caption=\"A\" /></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.False(result.IsSupported);
            Assert.Empty(result.Deltas);
            Assert.Contains(result.StructuralChanges, c => c.Kind == WebFormStructuralChangeKind.Moved);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_StructuralChangeKeepsAttributeDeltasEmpty()
        {            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><gxTextBlock ControlName=\"A\" Caption=\"A2\" /><gxButton ControlName=\"Btn\" Caption=\"Go\" /></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.False(result.IsSupported);
            Assert.Empty(result.Deltas);
            Assert.Contains(result.StructuralChanges, c => c.Kind == WebFormStructuralChangeKind.Added);
            Assert.Contains("structural", result.Reason);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_IdChurnPairsByControlName()
        {
            // Live: the SDK regenerates element ids on subtree materialization, so the
            // read representation carries different ids than the stored document. Identity
            // follows ControlName; id is never an authorable delta.
            const string currentXml =
                "<GxMultiForm><Form><body><gxTextBlock id=\"old-id\" ControlName=\"A\" Caption=\"A\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><gxTextBlock id=\"new-id\" ControlName=\"A\" Caption=\"A2\" /></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.True(result.IsSupported);
            Assert.Empty(result.StructuralChanges);
            var delta = Assert.Single(result.Deltas);
            Assert.Equal("A", delta.ControlName);
            Assert.Equal("Caption", delta.PropertyName);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_NestedAddRecordsWholeSubtree()
        {            const string currentXml =
                "<GxMultiForm><Form><body><table ControlName=\"T\" /></body></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><body><table ControlName=\"T\"><row><cell><gxTextBlock ControlName=\"W\" Caption=\"W\" /></cell></row></table></body></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.False(result.IsSupported);
            Assert.Empty(result.Deltas);
            Assert.Equal(3, result.StructuralChanges.Count);
            Assert.Contains(result.StructuralChanges, c => c.ControlType == "row" && c.Kind == WebFormStructuralChangeKind.Added);
            Assert.Contains(result.StructuralChanges, c => c.ControlType == "cell" && c.Kind == WebFormStructuralChangeKind.Added);
            var control = Assert.Single(result.StructuralChanges, c => c.ControlType == "gxTextBlock");
            Assert.Equal(WebFormStructuralChangeKind.Added, control.Kind);
            Assert.Equal("W", control.ControlName);
        }

        [Fact]
        public void DetectSupportedPropertyDeltas_LayoutReId_PairsByType()
        {
            // Live: stored and read ids differ with no intervening author change (the SDK
            // regenerates ids on materialization). Unnamed elements pair by type; id is
            // SDK-managed and never a delta.
            const string currentXml =
                "<GxMultiForm><Form><detail><layout id=\"old-layout\"><table id=\"old-table\" ControlName=\"T\" /></layout></detail></Form></GxMultiForm>";
            const string updatedXml =
                "<GxMultiForm><Form><detail><layout id=\"new-layout\"><table id=\"new-table\" ControlName=\"T\" /></layout></detail></Form></GxMultiForm>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(currentXml, updatedXml);

            Assert.True(result.IsSupported);
            Assert.Empty(result.Deltas);
            Assert.Empty(result.StructuralChanges);
        }
    }
}
