using System;
using System.Linq;
using System.Xml.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #360: no MCP route could set a TextBlock's Format on a plain WebPanel.
    ///
    /// <para>
    /// Three distinct defects, none of which the reporter could distinguish because each
    /// presented as "the property did not take":
    /// </para>
    ///
    /// <list type="number">
    /// <item>
    /// Control lookup was case-sensitive. <c>FindControlElement</c> asked XLinq for
    /// <c>Attribute("ControlName")</c>, which resolves case-sensitively, against a WebForm
    /// that spells it <c>controlName</c> — so every lookup returned null and
    /// <c>genexus_layout set_property</c> / <c>genexus_properties set</c> answered
    /// <c>ControlNotFound</c> for a control <c>get_tree</c> listed.
    /// </item>
    /// <item>
    /// <c>GxFormat</c> is not a WebForm attribute at all. The IDE stores it in a
    /// <c>PATTERN_ELEMENT_CUSTOM_PROPERTIES</c> payload, so a bare
    /// <c>format="HTML"</c> persisted, verified, and was then ignored by the generator.
    /// </item>
    /// <item>
    /// Nothing said so. The write reported success and the payload passed the schema
    /// hint scan, because the hint table listed <c>Format</c> and <c>GxFormat</c> as
    /// accepted attributes for legacy controls while knowing nothing about the modern
    /// <c>textblock</c> element at all.
    /// </item>
    /// </list>
    /// </summary>
    public class WebFormControlPropertyLookupTests
    {
        // The exact casing from the issue's IDE export.
        private const string IdexForm =
            "<GxMultiForm><Form><form>"
            + "<textblock controlName=\"RelCards\" caption=\"cards\" />"
            + "</form></Form></GxMultiForm>";

        [Fact]
        public void A_Control_Spelled_controlName_Is_Found_By_Its_Name()
        {
            var doc = XDocument.Parse(IdexForm);
            var element = LayoutService.FindControlElement(doc, "RelCards");

            Assert.NotNull(element);
            Assert.Equal("textblock", element.Name.LocalName);
        }

        [Fact]
        public void The_Uppercase_Spelling_Is_Found_Too()
        {
            // Same control, the other casing. Both must resolve, or the answer to "is my
            // control missing?" depends on a KB's generator vintage.
            var doc = XDocument.Parse(
                "<form><textblock ControlName=\"RelCards\" /></form>");
            Assert.NotNull(LayoutService.FindControlElement(doc, "RelCards"));
        }

        [Fact]
        public void The_Identity_Attributes_Are_Matched_Regardless_Of_Their_Spelling()
        {
            foreach (var spelling in new[] { "controlName", "ControlName", "CONTROLNAME" })
            {
                var doc = XDocument.Parse(
                    "<form><textblock " + spelling + "=\"RelCards\" /></form>");
                Assert.True(LayoutService.FindControlElement(doc, "RelCards") != null,
                    spelling + " did not resolve");
            }
        }

        [Fact]
        public void A_Control_That_Really_Is_Absent_Is_Still_Not_Found()
        {
            // The fix must not become "finds something for any name", which would turn a
            // typo into a silent write to the wrong control.
            var doc = XDocument.Parse(IdexForm);
            Assert.Null(LayoutService.FindControlElement(doc, "NoSuchControl"));
            Assert.Null(LayoutService.FindControlElement(doc, ""));
            Assert.Null(LayoutService.FindControlElement(doc, null));
        }

        [Fact]
        public void InternalName_And_Id_Still_Resolve()
        {
            Assert.NotNull(LayoutService.FindControlElement(
                XDocument.Parse("<form><textblock InternalName=\"A\" /></form>"), "A"));
            Assert.NotNull(LayoutService.FindControlElement(
                XDocument.Parse("<form><textblock id=\"B\" /></form>"), "B"));
        }

        [Fact]
        public void A_Namespace_Prefixed_Identity_Resolves()
        {
            // Resolved on LocalName, so gx:controlName has to work too.
            var doc = XDocument.Parse(
                "<form xmlns:gx=\"urn:x\"><textblock gx:controlName=\"RelCards\" /></form>");
            Assert.NotNull(LayoutService.FindControlElement(doc, "RelCards"));
        }

        [Fact]
        public void The_Delta_Detector_Pairs_A_Control_Across_Casings()
        {
            // Before, GetControlName had to enumerate spellings with ?? Attr(el,
            // "controlName") precisely because Attr was case-sensitive. A document that
            // reads with one casing and writes with the other used to look like every
            // control was removed and re-added, which is a structural diff.
            var current = "<form><textblock controlName=\"A\" caption=\"x\" /></form>";
            var updated = "<form><textblock ControlName=\"A\" caption=\"y\" /></form>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(current, updated);

            Assert.True(result.IsSupported, "casing alone must not read as a structural change: " + result.Reason);
            Assert.Empty(result.StructuralChanges);
            Assert.Contains(result.Deltas, d => d.PropertyName == "caption" && d.Value == "y");
        }

        [Fact]
        public void The_Delta_Detector_Does_Not_Invent_A_Delta_For_An_Identity_Only_Casing_Change()
        {
            var current = "<form><textblock controlName=\"A\" caption=\"x\" /></form>";
            var updated = "<form><textblock ControlName=\"A\" caption=\"x\" /></form>";

            var result = WebFormPropertyDeltaDetector.DetectSupportedPropertyDeltas(current, updated);

            Assert.True(result.IsSupported);
            Assert.Empty(result.Deltas);
        }
    }

    /// <summary>
    /// Issue #360's second half: <c>GxFormat</c> is an SDK control property carried by a
    /// custom-properties payload, not a WebForm attribute. Writing it bare persists, passes
    /// verification, and does nothing.
    /// </summary>
    public class WebFormCustomPropertyCarrierTests
    {
        // Verbatim from the issue's IDE export of WebRelMovimento after the user set
        // Format = Raw HTML in the IDE.
        private const string IdeWrittenForm =
            "<form><textblock controlName=\"RelCards\" caption=\"cards\" "
            + "PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"&lt;Properties&gt;&lt;Property&gt;"
            + "&lt;Name&gt;GxFormat&lt;/Name&gt;&lt;Value&gt;Raw HTML&lt;/Value&gt;"
            + "&lt;/Property&gt;&lt;/Properties&gt;\" /></form>";

        [Fact]
        public void A_Bare_Format_Attribute_Is_Flagged_With_The_Payload_To_Use_Instead()
        {
            var suspects = WebFormSchemaHints.ScanForRejectedAttributes(
                "<form><textblock controlName=\"RelCards\" format=\"HTML\" /></form>");

            var suspect = Assert.Single(suspects);
            Assert.Equal("textblock", suspect.Element);
            Assert.Equal("format", suspect.Attribute);
            Assert.Contains(WebFormSchemaHints.CustomPropertiesAttribute, suspect.Reason,
                StringComparison.Ordinal);
            Assert.Contains("no effect on the generated code", suspect.Reason, StringComparison.Ordinal);
            Assert.NotNull(suspect.Fix);
            Assert.Contains("GxFormat", suspect.Fix, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("GxFormat")]
        [InlineData("format")]
        [InlineData("Gxformat")]
        public void Every_Spelling_Of_The_Property_Is_Flagged(string attribute)
        {
            var suspects = WebFormSchemaHints.ScanForRejectedAttributes(
                "<form><textblock controlName=\"RelCards\" " + attribute + "=\"2\" /></form>");

            Assert.Contains(suspects, s => s.Attribute == attribute);
        }

        [Fact]
        public void The_Ide_Written_Form_Is_Not_Flagged()
        {
            // The IDE's own output must pass, or the warning would fire on correct input
            // and train the caller to ignore it.
            Assert.Empty(WebFormSchemaHints.ScanForRejectedAttributes(IdeWrittenForm));
        }

        [Fact]
        public void The_Carrier_Itself_Is_Never_Flagged_As_An_Unverified_Attribute()
        {
            var suspects = WebFormSchemaHints.ScanForRejectedAttributes(
                "<form><gxTextBlock controlName=\"A\" "
                + "PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"&lt;Properties&gt;&lt;/Properties&gt;\" /></form>");

            Assert.DoesNotContain(suspects,
                s => string.Equals(s.Attribute, WebFormSchemaHints.CustomPropertiesAttribute,
                    StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void The_Warning_Fires_On_An_Element_With_No_Hint_Table_Entry()
        {
            // textblock has no entry in the hint table, so the generic pass could not
            // judge it at all — which is why the specific check runs first.
            Assert.Null(WebFormSchemaHints.GetAcceptedAttributes("textblock"));

            var suspects = WebFormSchemaHints.ScanForRejectedAttributes(
                "<form><textblock controlName=\"A\" format=\"HTML\" /></form>");
            Assert.NotEmpty(suspects);
        }

        /// <summary>
        /// An attribute the hint table does know about is still judged the generic way.
        ///
        /// <para>
        /// Uses gxTextBlock rather than textblock because textblock has no hint entry at
        /// all — and "no hint registered, so nothing to say" is the documented, deliberate
        /// behaviour of this scanner (a missing hint is unverified, not a defect). The
        /// generic pass is exercised on the element it actually applies to.
        /// </para>
        /// </summary>
        [Fact]
        public void An_Unrelated_Attribute_On_A_Hinted_Control_Is_Still_Generic()
        {
            Assert.NotNull(WebFormSchemaHints.GetAcceptedAttributes("gxTextBlock"));

            var suspects = WebFormSchemaHints.ScanForRejectedAttributes(
                "<form><gxTextBlock controlName=\"A\" wibble=\"1\" /></form>");

            var suspect = Assert.Single(suspects);
            Assert.Equal("wibble", suspect.Attribute);
            Assert.Contains("not in the hint table", suspect.Reason, StringComparison.Ordinal);
            Assert.Null(suspect.Fix);
        }

        [Fact]
        public void A_Legacy_Control_That_Stores_Format_Plainly_Is_Not_Warned_About()
        {
            // gxTextBlock persists Format as an attribute and the generator reads it from
            // there. Warning about it would be a false positive, and a scanner that
            // warns on correct input teaches the caller to ignore it.
            Assert.NotNull(WebFormSchemaHints.GetAcceptedAttributes("gxTextBlock"));

            Assert.Empty(WebFormSchemaHints.ScanForRejectedAttributes(
                "<form><gxTextBlock controlName=\"A\" Format=\"Raw HTML\" /></form>"));
        }

        [Fact]
        public void A_Control_With_No_Such_Attribute_Is_Not_Flagged()
        {
            Assert.Empty(WebFormSchemaHints.ScanForRejectedAttributes(
                "<form><textblock controlName=\"RelCards\" caption=\"cards\" /></form>"));
        }

        [Theory]
        [InlineData("GxFormat")]
        [InlineData("Format")]
        [InlineData("gxformat")]
        [InlineData("  GxFormat  ")]
        [InlineData("ControlType")]
        [InlineData("ControlValues")]
        public void Set_Property_Refuses_These_With_An_Actionable_Message(string propertyName)
        {
            Assert.True(LayoutService.IsCustomPropertyOnly(propertyName),
                propertyName + " should be refused as a bare attribute");
        }

        [Theory]
        [InlineData("Caption")]
        [InlineData("Class")]
        [InlineData("Visible")]
        [InlineData("Width")]
        [InlineData("text")]
        [InlineData("")]
        [InlineData(null)]
        public void Ordinary_Layout_Properties_Are_Unaffected(string propertyName)
        {
            // A false positive here would block every existing layout mutation.
            Assert.False(LayoutService.IsCustomPropertyOnly(propertyName),
                propertyName + " must remain writable as an attribute");
        }

        [Fact]
        public void The_Carrier_Name_Matches_What_The_Ide_Writes()
        {
            // Pinned against the issue's verbatim export. A typo here would make the
            // warning name an attribute that does nothing.
            Assert.Equal("PATTERN_ELEMENT_CUSTOM_PROPERTIES",
                WebFormSchemaHints.CustomPropertiesAttribute);
            Assert.Contains(WebFormSchemaHints.CustomPropertiesAttribute, IdeWrittenForm,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The two write-path defects the live run in issue #360 exposed, both of which only
    /// became reachable once the case-sensitive lookup stopped short-circuiting.
    /// </summary>
    public class WebFormAttributeWriteTests
    {
        [Fact]
        public void An_Existing_Attribute_Is_Updated_In_Place_Not_Replaced()
        {
            var el = XElement.Parse("<textblock controlName=\"A\" caption=\"old\" />");

            LayoutService.WriteAttributeCaseInsensitive(el, "caption", "new");

            // The SDK's own export uses the lower-case spelling; replacing it with a
            // canonical one would change the document shape on every write.
            Assert.Equal("new", el.Attribute("caption").Value);
            Assert.Null(el.Attribute("Caption"));
        }

        [Fact]
        public void Case_Only_Duplicates_Are_Collapsed_Into_One()
        {
            // Observed live: after a Caption write on a control that spells the attribute
            // `caption`, the element held both, and the SDK save failed with
            // "Já foi adicionado um item com a mesma chave" — an SDK message that names
            // neither the attribute nor the control.
            var el = XElement.Parse("<textblock controlName=\"A\" caption=\"old\" Caption=\"old\" />");

            LayoutService.WriteAttributeCaseInsensitive(el, "Caption", "new");

            var captions = el.Attributes()
                .Where(a => string.Equals(a.Name.LocalName, "caption", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Single(captions);
            Assert.Equal("new", captions[0].Value);
        }

        [Fact]
        public void The_Survivor_Keeps_Its_Position_And_Unrelated_Attributes_Are_Untouched()
        {
            var el = XElement.Parse(
                "<textblock controlName=\"A\" caption=\"old\" class=\"c1\" Caption=\"old\" />");

            LayoutService.WriteAttributeCaseInsensitive(el, "Caption", "new");

            Assert.Equal(
                "<textblock controlName=\"A\" caption=\"new\" class=\"c1\" />",
                el.ToString());
        }

        [Fact]
        public void An_Absent_Attribute_Is_Added_Under_The_Requested_Name()
        {
            var el = XElement.Parse("<textblock controlName=\"A\" />");

            LayoutService.WriteAttributeCaseInsensitive(el, "Class", "c1");

            Assert.Equal("c1", el.Attribute("Class").Value);
            Assert.Equal(2, el.Attributes().Count());
        }

        [Fact]
        public void A_Namespace_Declaration_Is_Not_Treated_As_A_Duplicate()
        {
            // `class` is also a CLR keyword namespace; removing the declaration would
            // break the rest of the document, and matching on LocalName would find it.
            var el = XElement.Parse(
                "<textblock xmlns:cl=\"urn:c\" controlName=\"A\" class=\"c1\" cl:alias=\"x\" />");

            LayoutService.WriteAttributeCaseInsensitive(el, "class", "c2");

            Assert.Equal("c2", el.Attribute("class").Value);
            Assert.Equal("x", el.Attribute(XName.Get("alias", "urn:c")).Value);
            Assert.Equal("cl", el.GetPrefixOfNamespace("urn:c"));
        }

        [Fact]
        public void Nulls_Are_Ignored_Rather_Than_Throwing()
        {
            // The write path calls this on every set_property; an exception here would be
            // reported as an opaque LayoutMutationFailed.
            LayoutService.WriteAttributeCaseInsensitive(null, "caption", "x");
            LayoutService.WriteAttributeCaseInsensitive(XElement.Parse("<a/>"), null, "x");
            LayoutService.WriteAttributeCaseInsensitive(XElement.Parse("<a/>"), "", "x");
        }

        [Fact]
        public void A_Caption_Held_As_Tokens_Is_What_Verification_Reads()
        {
            // The SDK rewrote this control's caption as CaptionExpression Tokens, leaving
            // the plain `caption` attribute stale. Verifying against the attribute alone
            // read the old string, failed a correct write, and rolled it back — the
            // caller's change was discarded for a reason invisible in the response.
            var el = XElement.Parse(
                "<textblock controlName=\"A\" caption=\"stale\" "
                + "CaptionExpression=\"&lt;Tokens&gt;&lt;Token&gt;&lt;Type&gt;Constant&lt;/Type&gt;"
                + "&lt;Data&gt;&lt;![CDATA[fresh]]&gt;&lt;/Data&gt;&lt;/Token&gt;&lt;/Tokens&gt;\" />");

            Assert.Equal("fresh", LayoutService.ResolvePersistedCaption(el));
        }

        [Fact]
        public void A_Caption_Held_As_An_Attribute_Is_Still_Readable()
        {
            // The other family stores it there; tokens-first must not make that unreadable.
            var el = XElement.Parse("<textblock controlName=\"A\" caption=\"plain\" />");
            Assert.Equal("plain", LayoutService.ResolvePersistedCaption(el));
        }

        [Fact]
        public void A_Control_With_Neither_Caption_Home_Reads_As_Null()
        {
            Assert.Null(LayoutService.ResolvePersistedCaption(
                XDocument.Parse("<textblock controlName=\"A\" />").Root));
        }

        [Fact]
        public void A_Tokens_Caption_Is_Not_Misread_As_An_Empty_Caption()
        {
            // Guard on the precedence itself: if the tokens were an empty constant and the
            // attribute still held a real value, falling back would invent one.
            var el = XElement.Parse(
                "<textblock controlName=\"A\" caption=\"plain\" "
                + "CaptionExpression=\"&lt;Tokens&gt;&lt;Token&gt;&lt;Type&gt;Empty&lt;/Type&gt;"
                + "&lt;/Token&gt;&lt;/Tokens&gt;\" />");

            Assert.Equal("plain", LayoutService.ResolvePersistedCaption(el));
        }
    }

    /// <summary>
    /// The <c>SetProperty</c> refusal has to be at the call site, and the predicate tests
    /// above cannot see that.
    ///
    /// <para>
    /// This asserts the <b>exact</b> branch condition, inside the method body, with
    /// comments stripped — not that the predicate's name appears somewhere in the file.
    /// That distinction is load-bearing: an earlier "the guard string is present" assertion
    /// stayed green while the condition was short-circuited to <c>false</c>, so the guard
    /// was decoration. Here, removing the call or wrapping it in <c>false &amp;&amp;</c>
    /// changes the branch text and fails.
    /// </para>
    ///
    /// <para>
    /// What it still cannot prove: that the refusal reaches an MCP client.
    /// <c>SetProperty</c> needs a live Knowledge Base, so the envelope itself was verified
    /// live against KBTeste (a plain WebPanel with no pattern instance answered
    /// <c>ControlPropertyNeedsCustomProperties</c> for <c>propertyName=GxFormat</c>).
    /// </para>
    /// </summary>
    public class SetPropertyRefusalCallSiteTests
    {
        private static string SetPropertyBody() =>
            SourceAssert.MethodBody(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "LayoutService.cs"),
                "public string SetProperty(string target, string controlName, string propertyName, string value)");

        [Fact]
        public void The_Custom_Properties_Refusal_Gates_SetProperty_On_Its_Own()
        {
            string body = SourceAssert.NormaliseNewlines(SetPropertyBody());

            Assert.True(
                body.Contains("if (IsCustomPropertyOnly(propertyName))"),
                "SetProperty must branch on the predicate directly; a compound or negated "
                + "condition would let a custom-property write through with no effect. "
                + "Body was: " + body.Substring(0, Math.Min(400, body.Length)));
        }

        [Fact]
        public void The_Refusal_Names_The_Carrier_And_The_Valid_Values()
        {
            // The point of the refusal is that the caller learns the right representation
            // from the error. A bare "unsupported" would leave them where they started.
            string body = SourceAssert.NormaliseNewlines(SetPropertyBody());

            Assert.True(body.Contains("\"ControlPropertyNeedsCustomProperties\""),
                "the refusal needs a stable code clients can branch on");
            Assert.True(body.Contains("WebFormSchemaHints.CustomPropertiesAttribute"),
                "the hint must name the attribute the IDE actually writes");
            Assert.True(body.Contains("2=Raw HTML"),
                "the hint must give the numeric values GxFormat uses");
        }

        [Fact]
        public void The_Refusal_Happens_Before_The_Visual_Read()
        {
            // Ordering is the behaviour: the whole point is to fail cheaply, and to fail
            // before a control lookup that would otherwise answer ControlNotFound.
            string body = SourceAssert.NormaliseNewlines(SetPropertyBody());

            int guard = body.IndexOf("IsCustomPropertyOnly(propertyName)", StringComparison.Ordinal);
            int read = body.IndexOf("BeginVisualRead(target)", StringComparison.Ordinal);

            Assert.True(guard >= 0 && read >= 0, "both the guard and the read must be present");
            Assert.True(guard < read,
                "the refusal must come before BeginVisualRead, or a bare GxFormat write "
                + "still reports a control lookup failure instead of the real problem");
        }
    }

    /// <summary>
    /// The read-back sites in <c>LayoutService</c> must all go through the case-insensitive
    /// helpers, and that is not something the behavioural tests above can reach: the
    /// verification blocks are only entered with a live Knowledge Base, and one of them
    /// (the batch path) had already drifted while the single-property path was fixed.
    /// </summary>
    public class LayoutReadbackCaseInsensitivityTests
    {
        private static string Source() =>
            SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "LayoutService.cs"));

        [Fact]
        public void The_Only_Direct_Attribute_Read_Is_Inside_The_Helper_Itself()
        {
            // Attr is the single place allowed to use the case-sensitive XLinq overload -
            // as its first, fast attempt. Everywhere else must go through it, or the two
            // read paths disagree about a control's spelling.
            string src = Source();
            int helper = src.IndexOf("private static string Attr(XElement element, string name)",
                StringComparison.Ordinal);
            Assert.True(helper >= 0, "the Attr helper must exist");

            int stray = src.IndexOf(".Attribute(", StringComparison.Ordinal);
            while (stray >= 0)
            {
                // The helper's own body, plus the inspect_surface count, which is asserted
                // separately below so a reintroduction is noticed rather than tolerated.
                bool inHelper = stray > helper
                    && stray < src.IndexOf("internal static string ResolvePersistedCaption",
                        StringComparison.Ordinal);
                bool inDiagnostic = src.Substring(Math.Max(0, stray - 60), Math.Min(60, stray))
                    .Contains("controlAttrs");
                Assert.True(inHelper || inDiagnostic,
                    "a direct .Attribute( read at offset " + stray + " bypasses the "
                    + "case-insensitive Attr: " + src.Substring(stray, Math.Min(80, src.Length - stray)));
                stray = src.IndexOf(".Attribute(", stray + 1);
            }
        }

        [Fact]
        public void The_Surface_Diagnostic_Counts_Controls_Case_Insensitively()
        {
            // It reported zero controls for a form the SDK wrote, which is the same
            // "your control is not there" answer the issue was filed about - in the one
            // place a caller would go to check whether that was true.
            string src = Source();
            Assert.True(
                src.Contains("Count(e => Attr(e, \"ControlName\") != null)"),
                "inspect_surface's controlAttrs must count through Attr");
        }

        [Fact]
        public void The_Batch_Read_Back_Resolves_A_Caption_Through_The_Shared_Helper()
        {
            // The batch path had its own inline copy: a case-sensitive read, and a
            // CaptionExpression fallback that only ran when the attribute was absent. So a
            // batch Caption change on a control the SDK had rewritten as tokens compared
            // against the stale string and rolled back the entire batch - losing every
            // property in it, not just the one that disagreed.
            string body = SourceAssert.NormaliseNewlines(SourceAssert.MethodBody(
                Source(), "public string SetProperties(string target, JArray changes)"));

            Assert.True(body.Contains("ResolvePersistedCaption(persistedEl)"),
                "the batch read-back must resolve a caption through the shared helper");
            Assert.True(body.Contains("Attr(persistedEl, attrName)"),
                "the batch read-back must read other properties through the case-insensitive Attr");
            Assert.False(body.Contains("persistedEl.Attribute("),
                "a direct case-sensitive read in the batch read-back: " + body);
        }
    }
}
