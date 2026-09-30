using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// <c>SearchRankParallelismBenchmark</c> calibrates
    /// <c>SearchService.ParallelScanThreshold</c> - the candidate-set size at
    /// which PLINQ stops being overhead and starts winning. Its whole value rests
    /// on the per-item work being the production work, and it cannot call
    /// production: the Worker targets net48 against the GeneXus SDK, the benchmark
    /// project targets net10.0, so two scoring functions are hand-ported.
    ///
    /// Nothing enforced that port. This pins the half that is still exact.
    ///
    /// <see cref="CosineSimilarityIsStillTheProductionKernel"/> is that half: the
    /// benchmark's copy is semantically identical to
    /// <c>VectorService.CosineSimilarity</c>, so an equality test is meaningful and
    /// it means a production change can no longer leave the benchmark quietly
    /// measuring an older kernel.
    ///
    /// The other half, <c>SemanticScore</c>, is deliberately adapted - it drops the
    /// <c>typeFilter</c> parameter and, more importantly, has fallen behind
    /// production's length guards and its Table/attribute-member branch - so no
    /// equality test can hold it. That drift is documented on the method itself;
    /// <see cref="SemanticScoreIsDocumentedAsDrifted"/> keeps the note from being
    /// quietly deleted.
    /// </summary>
    public class SearchRankPortParityTests
    {
        private const string Benchmark = "src/GxMcp.Benchmarks/SearchRankParallelismBenchmark.cs";
        private const string VectorService = "src/GxMcp.Worker/Services/VectorService.cs";
        private const string SearchService = "src/GxMcp.Worker/Services/SearchService.cs";

        [Fact]
        public void CosineSimilarityIsStillTheProductionKernel()
        {
            // Comments and formatting are stripped so a reformat does not fail this,
            // and compared from the signature so the benchmark's `static` and the
            // service's `public` are not treated as a difference. Anything else -
            // an accumulator regrouped, a chunk size changed, the remainder loop
            // dropped - is a real divergence, and it is exactly the kind that makes
            // a calibration wrong without anything looking broken.
            string benchmark = Normalized(Body(RepoSource.Read(Benchmark), "float CosineSimilarity("));
            string production = Normalized(Body(RepoSource.Read(VectorService), "float CosineSimilarity("));

            Assert.NotNull(benchmark);
            Assert.NotNull(production);
            Assert.Equal(production, benchmark);
        }

        [Fact]
        public void SemanticScoreIsDocumentedAsDrifted()
        {
            // The adapted port cannot be pinned by an equality test, so the only
            // thing holding the knowledge in place is the note on the method.
            // Losing it restores a claim of fidelity that is no longer true.
            string src = RepoSource.Read(Benchmark);

            Assert.Contains("DRIFTED FROM PRODUCTION", src);
            Assert.Contains("LooksLikeAttributeName(term)", src);
            Assert.Contains("nameLen >= termLen", src);
            Assert.Contains("Re-port it before re-tuning the threshold.", src);

            // The header must stop asserting fidelity for the port as a whole.
            Assert.DoesNotContain("ported here verbatim", src);
        }

        [Fact]
        public void TheBenchmarkStillDocumentsWhyItPorts()
        {
            // net48 vs net10.0 is the reason the duplication exists. If that reason
            // ever goes away the port should too, so the reason is asserted as well
            // as the drift.
            string src = RepoSource.Read(Benchmark);

            Assert.Contains("net48", src);
            Assert.Contains("can't be referenced from this net10.0 project", src);
        }

        [Fact]
        public void TheProductionKernelIsStillTheOneTheCommentNames()
        {
            // The parity test above compares two files by path. This asserts the
            // comment's claim about where the kernel lives, so a moved or renamed
            // service is caught as "the benchmark points at a file that is not the
            // kernel any more" rather than as a silently different comparison.
            Assert.True(File.Exists(RepoSource.PathOf(VectorService)));
            Assert.True(File.Exists(RepoSource.PathOf(SearchService)));

            string production = RepoSource.Read(VectorService);
            Assert.Contains("public unsafe float CosineSimilarity(", production);
            Assert.Contains("private int CalculateSemanticScore(", RepoSource.Read(SearchService));
        }

        /// <summary>
        /// The braces-balanced text starting at <paramref name="signature"/>.
        /// </summary>
        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return null;

            int depth = 0;
            bool sawBrace = false;
            for (int i = at; i < source.Length; i++)
            {
                char c = source[i];
                if (c == '{') { depth++; sawBrace = true; }
                else if (c == '}') { depth--; if (sawBrace && depth == 0) return source.Substring(at, i - at + 1); }
            }
            return null;
        }

        /// <summary>
        /// Line comments removed, all whitespace collapsed. Reformatting must not
        /// fail the parity test; only a behavioural edit should.
        /// </summary>
        private static string Normalized(string body)
        {
            if (body == null) return null;
            return Regex.Replace(Regex.Replace(body, @"//[^\n]*", string.Empty), @"\s+", string.Empty);
        }

    }
}