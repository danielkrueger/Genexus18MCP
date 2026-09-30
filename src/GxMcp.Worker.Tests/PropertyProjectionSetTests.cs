using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// <c>projection=standard</c> is documented as the minimal projection plus
    /// more, and the code said so with a comment: the standard set used to spell
    /// the minimal nineteen out a second time under a "// Minimal set" marker.
    ///
    /// Nothing enforced it. Adding a name to
    /// <c>MinimalProjectionPropertyNames</c> and not to the standard list would
    /// leave a property that <c>projection=minimal</c> returns and
    /// <c>projection=standard</c> does not - so asking for more detail would return
    /// less, and the symptom would be a field missing from one shape of the same
    /// object rather than an error. The set is now built from the minimal one, and
    /// the containment holds by construction; these tests are what stop it being
    /// quietly undone.
    ///
    /// The extra names are pinned as an exact set. A projection is published
    /// output, so a name leaving it is a wire change and should have to be written
    /// down here rather than deleted from a list.
    /// </summary>
    public class PropertyProjectionSetTests
    {
        /// <summary>
        /// The standard-metadata names the standard projection adds. The minimal
        /// nineteen are not repeated: they are the other set, and repeating them
        /// here would reintroduce the copy this replaced.
        /// </summary>
        private static readonly string[] StandardExtras =
        {
            "IsNullable", "Nullable", "ALLOWNULL", "Autonumber", "Collection", "AttCollection",
            "Title", "Caption", "Module", "Parent", "Prefix", "ControlValues", "Values",
            "EnumValues", "ValidationFailedText", "Help", "Theme", "MasterPage", "Folder",
            "ExternalName", "ExternalNamespace", "CommitOnExit", "Protocol", "ExposeAsWebService",
            "SOAP", "REST", "ConnectivitySupport", "WebNotification", "WebUserExperience",
            "FormClass", "DefaultSelected", "Visible", "Enabled", "Class"
        };

        private static readonly string[] MinimalNames =
        {
            "Name", "Description", "DescriptionValue", "Type", "DataType", "DataTypeString",
            "Length", "AttMaxLen", "Decimals", "AttDec", "Signed", "AttSign", "Picture",
            "ATT_PICTURE", "Domain", "BasedOn", "DomainBasedOn", "DomainDefinition"
        };

        [Fact]
        public void TheStandardProjectionIsASupersetOfTheMinimalOne()
        {
            // The invariant. If this fails, a caller asking for the standard
            // projection gets strictly less than one asking for the minimal one.
            var missing = PropertyService.MinimalProjectionPropertyNames
                .Where(name => !PropertyService.StandardProjectionPropertyNames.Contains(name))
                .ToArray();

            Assert.True(missing.Length == 0,
                "standard projection is missing " + string.Join(", ", missing));
        }

        [Fact]
        public void TheStandardProjectionAddsExactlyTheDocumentedNames()
        {
            var extra = PropertyService.StandardProjectionPropertyNames
                .Except(PropertyService.MinimalProjectionPropertyNames, StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                StandardExtras.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                extra);
        }

        [Fact]
        public void TheMinimalProjectionStillHoldsItsOriginalNineteen()
        {
            // Pinned in both directions: a name added to the minimal set is a
            // published-output change too, and one removed changes every read.
            Assert.Equal(
                MinimalNames.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                PropertyService.MinimalProjectionPropertyNames
                    .OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void TheStandardProjectionIsStrictlyLargerThanTheMinimalOne()
        {
            // Otherwise "standard" is a second name for "minimal", and a client
            // choosing between them has been given a choice that does not exist.
            Assert.True(
                PropertyService.StandardProjectionPropertyNames.Count >
                PropertyService.MinimalProjectionPropertyNames.Count,
                "standard projection adds nothing to the minimal one");
        }

        [Fact]
        public void TheMinimalProjectionMatchesItsDuplicateSpellingsCaseInsensitively()
        {
            // The comparer is what makes the duplicate SDK spellings work: the
            // minimal set lists both Picture and ATT_PICTURE, because GeneXus
            // exposes the same property under different capitalisations on
            // different objects. A case-sensitive set would silently stop returning
            // one of them - a property that simply vanishes from a projection, with
            // no error and no other symptom.
            AssertMatches(PropertyService.MinimalProjectionPropertyNames, "minimal",
                "Picture", "ATT_PICTURE", "att_picture", "picture", "Att_Picture");
        }

        [Fact]
        public void TheStandardProjectionMatchesItsDuplicateSpellingsCaseInsensitively()
        {
            // The same, for the pair the standard set adds: IsNullable and
            // ALLOWNULL are the same property as GeneXus spells it either way.
            AssertMatches(PropertyService.StandardProjectionPropertyNames, "standard",
                "IsNullable", "ALLOWNULL", "allownull", "isnullable", "AlLoWnUlL");
        }

        private static void AssertMatches(HashSet<string> set, string label, params string[] names)
        {
            foreach (string name in names)
                Assert.True(set.Contains(name), label + " projection lost " + name);
        }

        [Fact]
        public void TheMinimalNamesAreSpelledOnceAndBothProjectionsShareTheBuilder()
        {
            // The two regressions this consolidation exists to prevent: the minimal
            // list being re-typed inside the standard one, and one projection
            // quietly acquiring a result key the other does not have.
            string source = RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs"));

            Assert.Equal(1, SourceAssert.Count(source, "private static HashSet<string> BuildStandardProjectionPropertyNames()"));
            Assert.Equal(1, SourceAssert.Count(source, "new HashSet<string>(MinimalProjectionPropertyNames, StringComparer.OrdinalIgnoreCase)"));
            Assert.Equal(1, SourceAssert.Count(source, "private static string BuildProjectionResult("));

            Assert.Equal(1, SourceAssert.Count(source, @"BuildProjectionResult(target, ""minimal"", props, MinimalProjectionPropertyNames, versionToken)"));
            Assert.Equal(1, SourceAssert.Count(source, @"BuildProjectionResult(target, ""standard"", props, StandardProjectionPropertyNames, versionToken)"));

            // One filter loop, so the emitted shape cannot differ between them.
            Assert.Equal(1, SourceAssert.Count(source, "filteredProps.Add((JObject)p.DeepClone());"));

            // The "first value wins for a name" rule the projections and the
            // unfiltered read share. The unfiltered path uses its own map name, so
            // exactly the two projection-shaped uses are inside the builder.
            Assert.Equal(1, SourceAssert.Count(source, @"[""projection""] = projection,"));
        }

    }
}
