using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Helpers;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// LockFileKey.Sanitize named the per-object lock file for both WritePipeline and
    /// MultiAgentLockService. If two copies disagreed on one character class the
    /// per-target mutual exclusion would fail as a silent lost update rather than an
    /// error, so the exact mapping is pinned here and the callers are pinned to the
    /// single helper.
    /// </summary>
    public class LockFileKeyTests
    {
        [Theory]
        [InlineData("MyPanel", "Source", "MyPanel__Source")]
        [InlineData("My Panel", "Source", "My_Panel__Source")]
        [InlineData("My/Panel", "WebForm", "My_Panel__WebForm")]
        [InlineData("Tx-1.0", "Events", "Tx-1.0__Events")]
        [InlineData("A:B", "Rule", "A_B__Rule")]
        public void Sanitize_KeepsTheSafeSetAndReplacesTheRest(string target, string part, string expected)
        {
            Assert.Equal(expected, LockFileKey.Sanitize(target, part));
        }

        [Fact]
        public void Sanitize_SubstitutesUnderscoreForNullTargetOrPart()
        {
            // "_" + "__" + part, target + "__" + "_", and "_" + "__" + "_".
            Assert.Equal("___Source", LockFileKey.Sanitize(null, "Source"));
            Assert.Equal("MyPanel___", LockFileKey.Sanitize("MyPanel", null));
            Assert.Equal("____", LockFileKey.Sanitize(null, null));
        }

        [Theory]
        // The whole point of the helper is this character class, so it is pinned
        // exhaustively rather than sampled: every character the set admits, and a
        // neighbour of each that it must replace.
        [InlineData("abcXYZ0189", "abcXYZ0189")]
        [InlineData("_-.", "_-.")]
        [InlineData(" \t\r\n", "____")]
        [InlineData("@#$%^&*()", "________")]
        [InlineData("+=[],;'\"", "__________")]
        [InlineData("\\/|?<>", "________")]
        // char.IsLetterOrDigit is Unicode-aware, so accented letters survive
        // verbatim. That is the pre-existing behavior of both original copies and
        // is preserved deliberately: a GeneXus object may legitimately be named
        // "Ordensaché", and folding it to "Ordensach_" would change the lock file
        // for an object that already has one.
        [InlineData("éüñ", "éüñ")]
        [InlineData("Ordensaché", "Ordensaché")]
        [InlineData("a b", "a_b")]
        public void Sanitize_PinsTheExactCharacterClass(string input, string expected)
        {
            Assert.Equal(expected, LockFileKey.Sanitize(input, null).Substring(0, expected.Length));
        }

        [Fact]
        public void Sanitize_ReplacesEveryCharacterOutsideTheSafeSet()
        {
            // One representative per boundary: a character one code point either
            // side of each admitted range must not survive verbatim.
            const string notSafe = " !\"#$%&'()*+,/:;<=>?@[\\]^`{|}~";
            foreach (char c in notSafe)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') continue;
                Assert.Equal("_", LockFileKey.Sanitize(c.ToString(), null).Substring(0, 1));
            }
        }

        [Fact]
        public void Sanitize_KeepsTheSeparatorUnambiguous()
        {
            // The "__" separator must not be produced by a substituted character,
            // or "a__b" and "a" + null part would collide on one lock file.
            Assert.NotEqual(
                LockFileKey.Sanitize("a", "b"),
                LockFileKey.Sanitize("a__b", null));
        }

        [Fact]
        public void Sanitize_IsStableAcrossCalls()
        {
            // Two processes must derive the same filename, so nothing may depend on
            // hash seeds, culture or call order.
            Assert.Equal(
                LockFileKey.Sanitize("Ordensaché", "Source"),
                LockFileKey.Sanitize("Ordensaché", "Source"));
        }

        [Fact]
        public void BothLockOwners_DelegateToTheOneKey()
        {
            string pipeline = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "WritePipeline.cs");
            string locks = RepoSource.Read("src", "GxMcp.Worker", "Services", "MultiAgentLockService.cs");

            foreach (string src in new[] { pipeline, locks })
            {
                Assert.Contains("LockFileKey.Sanitize(target, part)", src);
                Assert.DoesNotContain("char.IsLetterOrDigit(c)", src);
            }
        }

    }

    /// <summary>
    /// AttributeTypeApplier.ApplyFromDslType is now the only place a DSL type string
    /// reaches an Attribute, for both the Table and the Transaction parser. The two
    /// copies were not equivalent — only the Transaction one handled the SDK's
    /// shadowed <c>Attribute</c> property — so these pin the unwrap and the two
    /// outcomes, and a source guard pins the callers.
    /// </summary>
    public class ApplyFromDslTypeTests
    {
        // A stand-in for a DSL occurrence whose underlying Attribute is shadowed:
        // GetProperty("Attribute") on it throws AmbiguousMatchException, exactly as
        // the Artech SDK's TransactionAttribute does.
        public class ShadowedOccurrence
        {
            public object Attribute => throw new InvalidOperationException("shadowed");

            public object Inner { get; } = new object();
        }

        [Fact]
        public void NullInput_ReportsNoApplication()
        {
            Assert.False(AttributeTypeApplier.ApplyFromDslType(null, "Numeric(10)", null));
            Assert.False(AttributeTypeApplier.ApplyFromDslType(new object(), null, null));
            Assert.False(AttributeTypeApplier.ApplyFromDslType(new object(), "   ", null));
        }

        [Fact]
        public void UnrecognisedType_ReportsNoApplication()
        {
            Assert.False(AttributeTypeApplier.ApplyFromDslType(new object(), "NotATypeAtAll", null));
        }

        [Fact]
        public void DomainReferenceWithoutAModel_ReportsNoApplication()
        {
            // A Domain type needs the KB to resolve the name. No model means the
            // reference cannot land, which is a no-op rather than a throw.
            Assert.False(AttributeTypeApplier.ApplyFromDslType(new object(), "Domain(Region)", null));
        }

        // A derived type that shadows an inherited "Attribute" property: reflection
        // sees two candidates, a bare GetProperty throws AmbiguousMatchException, and
        // only a hierarchy walk resolves it. This is the shape of the Artech SDK's
        // TransactionAttribute.
        public class OccurrenceBase
        {
            public virtual string Attribute { get; set; } = "from-base";
        }

        public class DerivedOccurrence : OccurrenceBase
        {
            public override string Attribute { get; set; } = "from-derived";
        }

        [Fact]
        public void GetPropertyUnambiguous_ResolvesTheMostDerivedDeclaration()
        {
            // Direct guard on the mechanism rather than on a mock. The applier must
            // reach the underlying Attribute through this lookup, because a bare
            // GetProperty can return the wrong one on a derived type that shadows an
            // inherited property — which is what the SDK's TransactionAttribute does
            // and what the old Table parser could not survive.
            PropertyInfo resolved = AttributeTypeApplier.GetPropertyUnambiguous(
                typeof(DerivedOccurrence), "Attribute");
            Assert.NotNull(resolved);
            Assert.Equal(typeof(DerivedOccurrence), resolved.DeclaringType);

            // And the unwrap must actually read the derived value.
            Assert.Equal("from-derived", resolved.GetValue(new DerivedOccurrence()));

            Assert.Null(AttributeTypeApplier.GetPropertyUnambiguous(null, "Attribute"));
            Assert.Null(AttributeTypeApplier.GetPropertyUnambiguous(typeof(object), ""));
        }

        [Fact]
        public void ShadowedAttributeProperty_DoesNotThrow()
        {
            // The whole point of routing both parsers through GetPropertyUnambiguous:
            // the pre-existing Table copy called GetProperty directly inside a bare
            // catch, so a shadowed occurrence silently kept the occurrence itself and
            // then tried to write a type onto it.
            var occurrence = new ShadowedOccurrence();

            bool applied = AttributeTypeApplier.ApplyFromDslType(occurrence, "NotATypeAtAll", null);

            Assert.False(applied);
        }

        [Fact]
        public void BothParsers_DelegateToTheSingleApplier()
        {
            string table = RepoSource.Read("src", "GxMcp.Worker", "Parsers", "TableDslParser.cs");
            string transaction = RepoSource.Read("src", "GxMcp.Worker", "Parsers", "TransactionDslParser.cs");

            foreach (string src in new[] { table, transaction })
            {
                Assert.Contains("AttributeTypeApplier.ApplyFromDslType(", src);
                // Neither parser may resolve the Domain or write a primitive itself.
                Assert.DoesNotContain("GetByName", src);
                Assert.DoesNotContain("ApplyPrimitive", src);
                Assert.DoesNotContain("AmbiguousMatchException", src);
            }
        }

        [Fact]
        public void Applier_UnwrapsOccurrencesThroughTheUnambiguousLookup()
        {
            // The mutation this replaces swapped the ambiguity-resolving lookup for a
            // bare GetProperty. ApplyFromDslType is private-state, so this pins the
            // line the two parsers depend on: the only place that unwraps an
            // occurrence to its global Attribute must not be the one that throws on a
            // shadowed property.
            string helper = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "AttributeTypeApplier.cs");

            Assert.Contains(
                "GetPropertyUnambiguous(attributeOrOccurrence.GetType(), \"Attribute\")", helper);
            Assert.DoesNotContain(
                "attributeOrOccurrence.GetType().GetProperty(\"Attribute\")", helper);
        }

    }

    /// <summary>
    /// The attribute-declaration rendering rules were written out identically in the
    /// Table and the Transaction parser. This is a round-trip format - rendered here,
    /// parsed back by the same two parsers - so a rule that drifted between them would
    /// emit an attribute the other reads differently, which surfaces as an object
    /// that round-trips to something else rather than as an error.
    ///
    /// The two callers keep their own indentation, which genuinely differs: the
    /// Transaction parser nests its levels and prefixes each line.
    /// </summary>
    public class AttributeDeclarationRenderingTests
    {
        [Fact]
        public void ADeclarationIsNameMarkerColonType()
        {
            var text = AttributeDeclaration.Render("Codigo", string.Empty, "Numeric(10)", null, null, false);

            Assert.Equal("Codigo : Numeric(10)", text.Head);
            Assert.Equal(string.Empty, text.Comment);
        }

        [Fact]
        public void TheKeyMarkerSitsBetweenTheNameAndTheColon()
        {
            // GeneXus marks an index key with a trailing marker; it is part of the
            // declaration, not of the type.
            Assert.Equal("Codigo(+) : Character(20)",
                AttributeDeclaration.Render("Codigo", "(+)", "Character(20)", null, null, false).Head);
        }

        [Fact]
        public void ADescriptionThatRepeatsTheNameIsNotEmitted()
        {
            // The SDK fills Description in from the attribute's own name in the
            // common case, so echoing it would make every attribute noisier.
            Assert.Equal(string.Empty,
                AttributeDeclaration.Render("Codigo", string.Empty, "Numeric(10)", "Codigo", null, false).Comment);
        }

        [Fact]
        public void ADescriptionIsComparedToTheNameIgnoringCase()
        {
            Assert.Equal(string.Empty,
                AttributeDeclaration.Render("Codigo", string.Empty, "Numeric(10)", "codigo", null, false).Comment);
        }

        [Fact]
        public void ADistinctDescriptionIsQuotedIntoTheComment()
        {
            Assert.Equal(" // \"The order id\"",
                AttributeDeclaration.Render("Codigo", string.Empty, "Numeric(10)", "The order id", null, false).Comment);
        }

        [Fact]
        public void AFormulaBecomesAFormulaTag()
        {
            Assert.Equal(" // [Formula: Codigo+1]",
                AttributeDeclaration.Render("Codigo", string.Empty, "Numeric(10)", null, "Codigo+1", false).Comment);
        }

        [Fact]
        public void ANullableAttributeIsTagged()
        {
            Assert.Equal(" // [Nullable]",
                AttributeDeclaration.Render("Codigo", string.Empty, "Numeric(10)", null, null, true).Comment);
        }

        [Fact]
        public void TheDetailIsJoinedInDescriptionFormulaNullableOrder()
        {
            // The order is the contract: the parsers read this comment back, so a
            // reordering would change what a re-parse produces.
            Assert.Equal(" // \"Desc\", [Formula: F], [Nullable]",
                AttributeDeclaration.Render("A", string.Empty, "T", "Desc", "F", true).Comment);
        }

        [Fact]
        public void AnEmptyDescriptionOrFormulaIsSkipped()
        {
            Assert.Equal(" // [Nullable]",
                AttributeDeclaration.Render("A", string.Empty, "T", string.Empty, string.Empty, true).Comment);
        }

        [Fact]
        public void DetailNeverLeaksIntoTheHead()
        {
            // The Transaction parser concatenates Head and Comment separately, so a
            // description that ended up in the head would double up in its output.
            var text = AttributeDeclaration.Render("A", string.Empty, "T", "Desc", "F", true);

            Assert.DoesNotContain("Desc", text.Head);
            Assert.DoesNotContain("Formula", text.Head);
            Assert.DoesNotContain("Nullable", text.Head);
        }

        [Fact]
        public void BothParsers_DelegateToTheSingleRenderer()
        {
            foreach (string file in new[] { "TableDslParser.cs", "TransactionDslParser.cs" })
            {
                string src = RepoSource.Read("src", "GxMcp.Worker", "Parsers", file);

                Assert.Contains("AttributeDeclaration.Render(", src);
                // Neither parser may re-implement the rules it was handed.
                Assert.DoesNotContain("lineElements", src);
                Assert.DoesNotContain("[Formula: ", src);
                Assert.DoesNotContain("[Nullable]\"", src);
            }
        }

        [Fact]
        public void EachParser_KeepsItsOwnIndentation()
        {
            // The only genuine difference between the two call sites, and the reason
            // this is a helper returning two halves rather than a whole line.
            string table = RepoSource.Read("src", "GxMcp.Worker", "Parsers", "TableDslParser.cs");
            string transaction = RepoSource.Read("src", "GxMcp.Worker", "Parsers", "TransactionDslParser.cs");

            Assert.Contains("\"{0}{1}\", declaration.Head, declaration.Comment", table);
            Assert.Contains("indentStr", transaction);
            Assert.DoesNotContain("indentStr", table);
        }

        [Fact]
        public void TheRendererIsDefinedOnce()
        {
            string helper = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "AttributeDeclaration.cs");

            Assert.Equal(1, SourceAssert.Count(helper, "internal static AttributeDeclarationText Render("));
            foreach (string file in Directory.GetFiles(
                Path.GetDirectoryName(RepoSource.PathOf("src", "GxMcp.Worker", "Parsers", "TableDslParser.cs")), "*.cs"))
            {
                Assert.DoesNotContain("internal static AttributeDeclarationText Render(", File.ReadAllText(file));
            }
        }

    }

    /// <summary>
    /// <c>SafeTypeName</c> was a byte-identical private method on
    /// <c>SdkProbeService</c> and on <c>SdkSurfaceProbe</c>. Both use it to name an
    /// SDK type inside the failure path, so a divergence would make two reports of
    /// one object disagree about the type it was looking at.
    ///
    /// The safety is the point: it runs while reporting that reflection over the SDK
    /// already failed, so it must not fail the same way.
    /// </summary>
    public class SafeTypeNameTests
    {
        [Fact]
        public void ANullTypeIsNamedWithAQuestionMark()
        {
            Assert.Equal("?", SafeTypeName.Describe(null));
        }

        [Fact]
        public void ASimpleTypeIsNamedByItsSimpleName()
        {
            Assert.Equal("String", SafeTypeName.Describe(typeof(string)));
            Assert.Equal("Int32", SafeTypeName.Describe(typeof(int)));
        }

        [Fact]
        public void AConstructedGenericNamesItsDefinitionAndArguments()
        {
            Assert.Equal("Dictionary`2<String,List`1<Int32>>", SafeTypeName.Describe(typeof(Dictionary<string, List<int>>)));
        }

        [Fact]
        public void ANestedGenericRecurses()
        {
            Assert.Equal("Func`2<String,Func`2<Int32,Boolean>>", SafeTypeName.Describe(typeof(Func<string, Func<int, bool>>)));
        }

        [Fact]
        public void AnOpenGenericDefinitionNamesItsPlaceholders()
        {
            // Reached through a constructed type because typeof(List<>) is not legal.
            // GetGenericArguments on a definition returns the type parameters, which
            // is what makes the recursive call meaningful here.
            Type openList = typeof(List<int>).GetGenericTypeDefinition();

            Assert.Equal("List`1<T>", SafeTypeName.Describe(openList));
        }

        [Fact]
        public void TheSameTypeAlwaysGetsTheSameName()
        {
            // Two probe reports of one object have to agree; nothing here may depend
            // on hash seeds, culture or call order.
            Assert.Equal(SafeTypeName.Describe(typeof(Dictionary<string, int>)),
                         SafeTypeName.Describe(typeof(Dictionary<string, int>)));
        }

        [Fact]
        public void BothProbes_DelegateToTheSharedHelper()
        {
            foreach (string file in new[] { "SdkProbeService.cs", "SdkSurfaceProbe.cs" })
            {
                string src = RepoSource.Read("src", "GxMcp.Worker", "Services", file);

                Assert.Contains("SafeTypeName.Describe(", src);
                Assert.DoesNotContain("private static string SafeTypeName", src);
                Assert.DoesNotContain("GetGenericTypeDefinition", src);
            }
        }

        [Fact]
        public void TheHelperIsDefinedOnceAndKeepsItsSafetyNet()
        {
            string helper = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "SafeTypeName.cs");

            Assert.Equal(1, SourceAssert.Count(helper, "internal static string Describe(Type type)"));
            Assert.Contains("catch { return \"?\"; }", helper);

            foreach (string file in Directory.GetFiles(
                Path.GetDirectoryName(RepoSource.PathOf("src", "GxMcp.Worker", "Services", "SdkProbeService.cs")), "*.cs"))
            {
                Assert.DoesNotContain("internal static string Describe(Type type)", File.ReadAllText(file));
            }
        }

    }
}
