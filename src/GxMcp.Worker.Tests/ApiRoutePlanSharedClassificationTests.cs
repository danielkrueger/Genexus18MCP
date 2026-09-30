using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A route plan is built two ways - by cloning every route under a prefix, or
    /// from an explicit list of source/target pairs - and the two paths carried
    /// byte-identical copies of the candidate classification and of the edit
    /// application. They are now <c>ClassifyRouteCandidate</c> and
    /// <c>ApplyRouteChanges</c>.
    ///
    /// Two properties carry the weight here:
    ///
    /// <para><b>Bucketing.</b> Each candidate must land in exactly one of added,
    /// unchanged, updated, conflicting. Two buckets produces a partial rewrite of
    /// the API declaration; no bucket loses a route the caller asked for.</para>
    ///
    /// <para><b>Splice order.</b> Replacements are spliced from the highest source
    /// offset downwards, because each one replaces a run of a different length and
    /// so shifts every offset after it. Applying them the other way up rewrites the
    /// wrong span of source - which corrupts the declaration instead of failing.
    /// The only pre-existing replacement test had a single replacement, where both
    /// orders give the same answer; these have two, with opposite length deltas, so
    /// they actually discriminate.</para>
    ///
    /// The two paths deliberately differ in one place and
    /// <see cref="TheClonePathResetsCandidateSourceOnConflict"/> pins that
    /// difference so it is not "tidied" away later.
    /// </summary>
    public class ApiRoutePlanSharedClassificationTests
    {
        /// <summary>
        /// Two inbound routes, two existing reverse routes whose source text is
        /// longer for the first and shorter for the second - so a wrongly-ordered
        /// splice shifts the second target's offset and lands between tokens.
        /// </summary>
        private const string TwoReplacementSource = @"api OrdersApi {
[RestMethod(POST), RestPath(""/orders/inbound/"")] inbound_post(in:&Request) => operational.OrderInbound(&Request);
[RestMethod(GET), RestPath(""/orders/inbound/&codOrder"")] inbound_get(in:&codOrder) => operational.GetOrder(&codOrder);
[RestMethod(POST), RestPath(""/orders/reverse/some/considerably/longer/legacy/path/"")] reverse_post(in:&Request) => operational.Leg(&Request);
[RestMethod(GET), RestPath(""/r"")] reverse_get(in:&codOrder) => operational.Other(&codOrder);
}";

        [Fact]
        public void TheClonePath_SplicesEveryReplacementAtItsOwnOffset()
        {
            var plan = ApiIntrospectService.BuildApiRoutePlan(
                TwoReplacementSource, "inbound", "reverse", updateExisting: true);

            Assert.Empty(plan.Conflicts);
            Assert.Equal(2, plan.Updated.Count);
            Assert.Equal(new[] { "reverse_post", "reverse_get" }, plan.Updated.Select(r => r.MethodName));

            // Both replacements landed whole at the right offsets...
            Assert.Contains(
                @"[RestMethod(POST), RestPath(""/orders/reverse/"")] reverse_post(in:&Request) => operational.OrderInbound(&Request);",
                plan.CandidateSource);
            Assert.Contains(
                @"[RestMethod(GET), RestPath(""/orders/reverse/&codOrder"")] reverse_get(in:&codOrder) => operational.GetOrder(&codOrder);",
                plan.CandidateSource);

            // ...and the untargeted routes were not disturbed. A wrong-order splice
            // shows up here as a truncated or spliced-together declaration.
            Assert.Contains(
                @"[RestMethod(GET), RestPath(""/orders/inbound/&codOrder"")] inbound_get(in:&codOrder) => operational.GetOrder(&codOrder);",
                plan.CandidateSource);
            Assert.DoesNotContain("considerably/longer/legacy", plan.CandidateSource);
            Assert.DoesNotContain(@"""]])", plan.CandidateSource);
        }

        [Fact]
        public void TheExplicitPath_SplicesEveryReplacementTheSameWay()
        {
            // The same two replacements, reached through the explicit-routes path,
            // must produce the same source. Before consolidation these were two
            // copies of the same code and nothing compared them.
            var requested = new JArray
            {
                new JObject
                {
                    ["sourceMethod"] = "inbound_post",
                    ["method"] = "reverse_post",
                    ["route"] = "/orders/reverse/",
                    ["verb"] = "POST"
                },
                new JObject
                {
                    ["sourceMethod"] = "inbound_get",
                    ["method"] = "reverse_get",
                    ["route"] = "/orders/reverse/&codOrder",
                    ["verb"] = "GET"
                }
            };

            var plan = ApiIntrospectService.BuildApiRoutePlan(
                TwoReplacementSource, null, null, updateExisting: true, requested);

            Assert.Empty(plan.Conflicts);
            Assert.Equal(new[] { "reverse_post", "reverse_get" }, plan.Updated.Select(r => r.MethodName));

            var viaPrefixes = ApiIntrospectService.BuildApiRoutePlan(
                TwoReplacementSource, "inbound", "reverse", updateExisting: true);

            Assert.Equal(viaPrefixes.CandidateSource, plan.CandidateSource);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ADifferingExistingRoute_IsAConflictUnlessUpdating(bool updateExisting)
        {
            var requested = new JArray
            {
                new JObject
                {
                    ["sourceMethod"] = "inbound_post",
                    ["method"] = "reverse_post",
                    ["route"] = "/orders/reverse/",
                    ["verb"] = "POST"
                }
            };

            var plan = ApiIntrospectService.BuildApiRoutePlan(
                TwoReplacementSource, null, null, updateExisting, requested);

            if (updateExisting)
            {
                Assert.Empty(plan.Conflicts);
                Assert.Single(plan.Updated);
            }
            else
            {
                Assert.Single(plan.Conflicts);
                Assert.Empty(plan.Updated);
                Assert.Contains(
                    "method already exists with different content",
                    plan.Conflicts[0]?.ToString());
            }
        }

        [Fact]
        public void AnIdenticalExistingRoute_IsUnchangedNotUpdated()
        {
            const string source = @"api OrdersApi {
[RestMethod(POST), RestPath(""/orders/reverse/"")] reverse_post(in:&Request) => operational.OrderInbound(&Request);
[RestMethod(POST), RestPath(""/orders/inbound/"")] inbound_post(in:&Request) => operational.OrderInbound(&Request);
}";
            var requested = new JArray
            {
                new JObject
                {
                    ["sourceMethod"] = "inbound_post",
                    ["method"] = "reverse_post",
                    ["route"] = "/orders/reverse/",
                    ["verb"] = "POST"
                }
            };

            var plan = ApiIntrospectService.BuildApiRoutePlan(source, null, null, true, requested);

            Assert.Empty(plan.Conflicts);
            Assert.Empty(plan.Updated);
            Assert.Single(plan.Unchanged);
            Assert.Equal("reverse_post", plan.Unchanged[0].MethodName);
        }

        [Fact]
        public void ADifferentMethodOnTheSameVerbAndPath_IsAConflict()
        {
            const string source = @"api OrdersApi {
[RestMethod(POST), RestPath(""/orders/inbound/"")] inbound_post(in:&Request) => operational.OrderInbound(&Request);
[RestMethod(POST), RestPath(""/orders/reverse/"")] some_other_post(in:&Request) => operational.Something(&Request);
}";
            var requested = new JArray
            {
                new JObject
                {
                    ["sourceMethod"] = "inbound_post",
                    ["method"] = "reverse_post",
                    ["route"] = "/orders/reverse/",
                    ["verb"] = "POST"
                }
            };

            var plan = ApiIntrospectService.BuildApiRoutePlan(source, null, null, true, requested);

            Assert.Empty(plan.Updated);
            Assert.True(plan.Conflicts.Count > 0,
                "expected a verb+path conflict, got conflicts=["
                + string.Join(" | ", plan.Conflicts.Select(c => c?.ToString()))
                + "] added=[" + string.Join(" | ", plan.Added.Select(r => r.MethodName)) + "]");
            Assert.Contains("reverse_post (POST /orders/reverse/)", plan.Conflicts[0]?.ToString());
        }

        [Fact]
        public void AnUnknownSourceMethod_IsAConflictAndNothingIsApplied()
        {
            const string source = @"api OrdersApi {
[RestMethod(POST), RestPath(""/orders/inbound/"")] inbound_post(in:&Request) => operational.OrderInbound(&Request);
}";
            var requested = new JArray
            {
                new JObject
                {
                    ["sourceMethod"] = "does_not_exist",
                    ["method"] = "reverse_post",
                    ["route"] = "/orders/reverse/",
                    ["verb"] = "POST"
                }
            };

            var plan = ApiIntrospectService.BuildApiRoutePlan(source, null, null, true, requested);

            Assert.Contains("does_not_exist (source method not found)", plan.Conflicts[0]?.ToString());
            Assert.Empty(plan.Added);
            Assert.Equal(source, plan.CandidateSource);
        }

        [Fact]
        public void TheClonePathResetsCandidateSourceOnConflict()
        {
            // The prefix-driven path restores the caller's source when any target
            // collides, so a conflicting plan can never be written back. This is
            // the one place the two paths differ and it is deliberate: the explicit
            // path is entered with the plan's source already set and reports the
            // original text on a validation error instead.
            var plan = ApiIntrospectService.BuildApiRoutePlan(
                TwoReplacementSource, "inbound", "reverse", updateExisting: false);

            Assert.NotEmpty(plan.Conflicts);
            Assert.Equal(TwoReplacementSource, plan.CandidateSource);
        }

        [Fact]
        public void ACandidateLandsInExactlyOneBucket()
        {
            var requested = new JArray
            {
                new JObject
                {
                    ["sourceMethod"] = "inbound_post",
                    ["method"] = "reverse_post",
                    ["route"] = "/orders/reverse/",
                    ["verb"] = "POST"
                },
                new JObject
                {
                    ["sourceMethod"] = "inbound_get",
                    ["method"] = "reverse_get",
                    ["route"] = "/orders/reverse/&codOrder",
                    ["verb"] = "GET"
                }
            };

            var plan = ApiIntrospectService.BuildApiRoutePlan(
                TwoReplacementSource, null, null, true, requested);

            var seen = new List<string>();
            seen.AddRange(plan.Added.Select(r => r.MethodName));
            seen.AddRange(plan.Updated.Select(r => r.MethodName));
            seen.AddRange(plan.Unchanged.Select(r => r.MethodName));

            Assert.Equal(2, seen.Count);
            Assert.Equal(seen.Count, seen.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(new[] { "reverse_post", "reverse_get" }, seen);
        }

        [Fact]
        public void AdditionsAreAppendedAfterReplacements()
        {
            // One updated route plus one added route: the addition has to land
            // inside the declaration, after the splice, not at the splice site.
            const string source = @"api OrdersApi {
[RestMethod(POST), RestPath(""/orders/inbound/"")] inbound_post(in:&Request) => operational.OrderInbound(&Request);
[RestMethod(GET), RestPath(""/orders/inbound/&codOrder"")] inbound_get(in:&codOrder) => operational.GetOrder(&codOrder);
[RestMethod(POST), RestPath(""/orders/reverse/legacy/"")] reverse_post(in:&Request) => operational.Leg(&Request);
}";
            var requested = new JArray
            {
                new JObject
                {
                    ["sourceMethod"] = "inbound_post",
                    ["method"] = "reverse_post",
                    ["route"] = "/orders/reverse/",
                    ["verb"] = "POST"
                },
                new JObject
                {
                    ["sourceMethod"] = "inbound_get",
                    ["method"] = "reverse_get",
                    ["route"] = "/orders/reverse/&codOrder",
                    ["verb"] = "GET"
                }
            };

            var plan = ApiIntrospectService.BuildApiRoutePlan(source, null, null, true, requested);

            Assert.Empty(plan.Conflicts);
            Assert.Single(plan.Updated);
            Assert.Single(plan.Added);
            Assert.Contains(@"reverse_post(in:&Request) => operational.OrderInbound(&Request);", plan.CandidateSource);
            Assert.Contains("reverse_get(in:&codOrder) => operational.GetOrder(&codOrder);", plan.CandidateSource);
            Assert.EndsWith("}", plan.CandidateSource.TrimEnd());
        }

        [Fact]
        public void TheClassificationAndApplyBlocksExistOnlyOnce()
        {
            // Two call sites, one implementation each. A third copy of either
            // re-introduces the drift this removed.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "ApiIntrospectService.cs");

            Assert.Equal(1, CountOccurrences(src, "private static void ClassifyRouteCandidate("));
            Assert.Equal(1, CountOccurrences(src, "private static string ApplyRouteChanges("));

            // One call per plan builder. ClassifyRouteCandidate is always called
            // with the same argument list; ApplyRouteChanges is called with the
            // base source each builder resolved for itself.
            Assert.Equal(2, CountOccurrences(src, "ClassifyRouteCandidate(plan, current, candidate, updateExisting, replacements, additions)"));
            Assert.Equal(1, CountOccurrences(src, "ApplyRouteChanges(source, replacements, additions)"));
            Assert.Equal(1, CountOccurrences(src, "ApplyRouteChanges(plan.CandidateSource, replacements, additions)"));

            // And neither caller re-implements the shape it was handed.
            Assert.Equal(1, CountOccurrences(src, "plan.Conflicts.Add(candidate.MethodName + \" (method already exists with different content)\");"));
            Assert.Equal(1, CountOccurrences(src, "replacements.OrderByDescending(c => c.Existing.SourceIndex)"));
            Assert.Equal(1, CountOccurrences(src, "string.Equals(r.Verb, candidate.Verb, StringComparison.OrdinalIgnoreCase)"));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

    }
}
