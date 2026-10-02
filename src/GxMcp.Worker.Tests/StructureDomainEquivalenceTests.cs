using System;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class StructureDomainEquivalenceTests
    {
        private const string Requested = "SampleTrn\n{\n    SampleId* : Numeric(4)\n    SampleTrnDz* : SampleDomain // \"Dz\"\n    SampleName : Character(20)\n}";
        private const string Persisted = "SampleTrn\n{\n    SampleId* : NUMERIC(4)\n    SampleTrnDz* : CHARACTER(10) // \"Dz\"\n    SampleName : Character(20)\n}";

        private static Func<string, string> BasedOn(string attr, string domain)
            => name => string.Equals(name, attr, StringComparison.OrdinalIgnoreCase) ? domain : null;

        private static bool Matches(string requested, string persisted, Func<string, string> lookup)
        {
            string canonical = StructureDomainEquivalence.Canonicalize(requested, persisted, lookup);
            return WriteService.EvaluatePersistedVerification(canonical, persisted, false, null, "exact", "Structure").Matches;
        }

        [Fact]
        public void DomainLine_WhoseAttributeIsBasedOnThatDomain_Matches()
        {
            var persistedWithSameCase = Persisted.Replace("NUMERIC(4)", "Numeric(4)");
            Assert.True(Matches(Requested, persistedWithSameCase, BasedOn("SampleTrnDz", "sampledomain")));
        }

        [Fact]
        public void DomainLine_WhoseAttributeIsNotDomainBased_Mismatches()
        {
            Assert.False(Matches(Requested, Persisted.Replace("NUMERIC(4)", "Numeric(4)"), BasedOn("SampleTrnDz", null)));
        }

        [Fact]
        public void DomainLine_WhoseAttributeUsesAnotherDomain_Mismatches()
        {
            Assert.False(Matches(Requested, Persisted.Replace("NUMERIC(4)", "Numeric(4)"), BasedOn("SampleTrnDz", "OtherDomain")));
        }

        [Fact]
        public void UnknownIdentifier_Mismatches()
        {
            Assert.False(Matches(Requested, Persisted.Replace("NUMERIC(4)", "Numeric(4)"), name => null));
        }

        [Fact]
        public void NonDomainDifferenceElsewhere_StillMismatches()
        {
            var persisted = Persisted.Replace("NUMERIC(4)", "Numeric(4)").Replace("Character(20)", "Character(30)");
            Assert.False(Matches(Requested, persisted, BasedOn("SampleTrnDz", "SampleDomain")));
        }

        [Fact]
        public void KeyMarkerAndDescriptionAreKept()
        {
            string canonical = StructureDomainEquivalence.Canonicalize(Requested, Persisted, BasedOn("SampleTrnDz", "SampleDomain"));
            Assert.Contains("    SampleTrnDz* : CHARACTER(10) // \"Dz\"", canonical);
            Assert.Contains("SampleId* : Numeric(4)", canonical);
        }

        [Fact]
        public void CrLfLinesAreHandled()
        {
            string canonical = StructureDomainEquivalence.Canonicalize(
                Requested.Replace("\n", "\r\n"), Persisted.Replace("\n", "\r\n"), BasedOn("SampleTrnDz", "SampleDomain"));
            Assert.Contains("SampleTrnDz* : CHARACTER(10) // \"Dz\"\r\n", canonical);
        }
    }
}
