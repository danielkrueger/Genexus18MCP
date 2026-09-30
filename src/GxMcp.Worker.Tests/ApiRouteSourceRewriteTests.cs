using System;
using System.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// A route rename rewrites the method name and the path into the same copy of
    /// the API's source block, and the order the two replacements happen in is the
    /// entire content of the operation.
    ///
    /// The recorded offsets are positions in the <em>original</em> text. Replacing
    /// the later one first would shift the earlier one's position - that replacement
    /// changes the length of everything after it - and the second one would then
    /// land in the wrong place, silently producing source that still carries the old
    /// prefix, or that has lost the words between the two spans. That is not a
    /// compile error and not a thrown exception; it is a written file with
    /// something wrong in the middle of it.
    ///
    /// The branch decides which order is right, so it only ever gets one of them
    /// from parsed source: the attributes come first in a route block, which puts
    /// the path span before the method span. Both sides are covered below - the
    /// parsed shape, and a shape with the path in the call where the method span
    /// really does come first - and the expected output of each is asserted as an
    /// exact string. That is what makes the assertion a test of the ordering: apply
    /// either order to both shapes and one of the two exact strings no longer holds.
    ///
    /// The production helpers are <c>internal</c> so these call them rather than
    /// restate the rule. A test that reimplemented the offset arithmetic would pass
    /// with a wrong rule in production, which is the failure this exists to
    /// prevent, and the parsed fixture comes from <c>ParseApiRoutes</c> rather than
    /// being built by hand, so a change to the parser's recorded offsets is what
    /// these would notice.
    /// </summary>
    public class ApiRouteSourceRewriteTests
    {
        /// <summary>
        /// Two routes in the shape the API source is written, each with a longer
        /// name than the last, so a rewrite that ran off its own block would be
        /// caught against a differently-sized neighbour.
        /// </summary>
        private const string ApiSource = @"api AccountsApi {
[RestMethod(GET), RestPath(""/api/Users"")] users_getAll() => operational.GetUsers();
[RestMethod(POST), RestPath(""/api/Orders"")] orders_add(in:&Order) => operational.AddOrder(&Order);
}";

        /// <summary>
        /// A block whose path lives in the call rather than in the attributes, so
        /// the method span comes first. The parser cannot produce this shape - it
        /// matches the path inside the attribute block by construction - but it is
        /// the other side of the branch, and a guard nobody ever runs is a guess.
        /// </summary>
        private const string PathInCallBlock = @"users_getAll() => operational.GetUsers(""/api/Users"");";

        /// <summary>
        /// The offsets the parser records address the two spans the rewrite replaces,
        /// and they address them in the original text.
        ///
        /// This is the invariant the ordering exists to protect, so it is asserted
        /// on its own: if it stopped holding - because the parser started measuring
        /// against a rewritten block, or the path group began matching somewhere
        /// else - then every route rename would write corrupted source and nothing
        /// downstream would say why.
        /// </summary>
        [Fact]
        public void TheRecordedOffsetsAddressTheTwoSpansInTheOriginalText()
        {
            var routes = ApiIntrospectService.ParseApiRoutes(ApiSource);

            foreach (var route in routes)
            {
                Assert.Equal(route.MethodName, SpanAt(route, route.MethodOffset, route.MethodName.Length));
                Assert.Equal(route.Path, SpanAt(route, route.PathOffset, route.PathLength));

                // The recorded span is exactly the path, leading slash included, which
                // is what the replacement text carries too. A span that excluded the
                // slash would replace "/api/Users" with "/api/Accounts" over the span
                // "api/User" and leave a trailing "s" behind.
                Assert.Equal(route.Path.Length, route.PathLength);

                // Both spans are inside this route's own block, not the next one.
                Assert.True(route.MethodOffset + route.MethodName.Length <= route.SourceText.Length);
                Assert.True(route.PathOffset + route.PathLength <= route.SourceText.Length);
            }
        }

        /// <summary>
        /// Parsed source always puts the attributes before the declaration, so the
        /// path span precedes the method span and one side of the branch is the live
        /// one. Asserted rather than assumed: if the parser's shape ever changed then
        /// the branch treated as defensive would become the one that runs, and the
        /// other shape below would only be covering one nobody hits.
        /// </summary>
        [Fact]
        public void ParsedRoutesAlwaysPutThePathBeforeTheMethodName()
        {
            var routes = ApiIntrospectService.ParseApiRoutes(ApiSource);

            Assert.Equal(2, routes.Count);
            Assert.All(routes, route => Assert.True(route.MethodOffset > route.PathOffset));
        }

        /// <summary>
        /// The parsed shape, where the path comes first: the method span is replaced
        /// first, then the path span, and the result is asserted whole.
        /// </summary>
        [Fact]
        public void AParsedRouteRewritesBothSpansInTheOffsetOrder()
        {
            ApiIntrospectService.ApiRoute route = ApiIntrospectService.ParseApiRoutes(ApiSource).First();

            string block = ApiIntrospectService.RewriteApiRouteSource(route, "accounts_getAll", "/api/Accounts");

            Assert.Equal(
                @"[RestMethod(GET), RestPath(""/api/Accounts"")] accounts_getAll() => operational.GetUsers();",
                block);
        }

        /// <summary>
        /// The other side of the branch: the path span really does come second, so
        /// the replacement order inverts. The method name grows by two characters and
        /// the path by three, which is enough to push a stale offset into the wrong
        /// span and is asserted as an exact string for that reason.
        /// </summary>
        [Fact]
        public void ARouteWhoseMethodComesFirstRewritesBothSpansInTheOtherOrder()
        {
            ApiIntrospectService.ApiRoute route = new ApiIntrospectService.ApiRoute
            {
                MethodName = "users_getAll",
                Verb = "GET",
                Path = "/api/Users",
                SourceText = PathInCallBlock,
                MethodOffset = 0,
                PathOffset = PathInCallBlock.IndexOf("/api/Users", StringComparison.Ordinal),
                PathLength = "/api/Users".Length
            };

            Assert.False(route.MethodOffset > route.PathOffset);

            string block = ApiIntrospectService.RewriteApiRouteSource(route, "accounts_getAll", "/api/Accounts");

            Assert.Equal(
                @"accounts_getAll() => operational.GetUsers(""/api/Accounts"");",
                block);
        }

        /// <summary>
        /// A rename that leaves both spans the same length as they were. It is a
        /// separate case because a rewrite could pass the exact-string assertions
        /// above on a rule that ignores length entirely, and that rule would be wrong
        /// for every real rename, which changes the name.
        /// </summary>
        [Fact]
        public void ASameLengthRenameAlsoLandsBothSpans()
        {
            ApiIntrospectService.ApiRoute route = ApiIntrospectService.ParseApiRoutes(ApiSource).First();

            string block = ApiIntrospectService.RewriteApiRouteSource(route, "users_getAll", "/api/Users");

            // Unchanged, and nothing double-applied.
            Assert.Equal(route.SourceText, block);
            Assert.Equal(1, SourceAssert.Count(block, "users_getAll"));
            Assert.Equal(1, SourceAssert.Count(block, "/api/Users"));
        }

        /// <summary>
        /// The rewrite is confined to its own route. The second route sits further
        /// along the text with different offsets, so a rewrite that ran past its
        /// block's end would rename the neighbour instead.
        /// </summary>
        [Fact]
        public void TheRewriteDoesNotReachIntoTheNextRoute()
        {
            var routes = ApiIntrospectService.ParseApiRoutes(ApiSource);

            string block = ApiIntrospectService.RewriteApiRouteSource(routes[0], "accounts_getAll", "/api/Accounts");

            Assert.DoesNotContain("orders_add", block);
            Assert.DoesNotContain("/api/Orders", block);
            Assert.DoesNotContain("AddOrder", block);
        }

        /// <summary>
        /// The structural half: both clone paths - the prefix substitution and the
        /// caller's own names - go through the same rewrite, or two routes differing
        /// only in where their names came from would be assembled by two rules.
        /// </summary>
        [Fact]
        public void TheTwoRouteBuildersShareOneRewrite()
        {
            string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ApiIntrospectService.cs")));

            // One rule, stated once, used once.
            Assert.Equal(1, SourceAssert.Count(source, "source.MethodOffset > source.PathOffset"));
            Assert.Equal(1, SourceAssert.Count(source, "internal static string RewriteApiRouteSource(ApiRoute source, string methodName, string path)"));
            Assert.Equal(1, SourceAssert.Count(source, "SourceText = RewriteApiRouteSource(source, methodName, path)"));

            // The prefix variant computes the new names and delegates; it does not
            // repeat the rewrite or the assembly.
            Assert.Equal(1, SourceAssert.Count(source, "return CloneApiRouteTo(source, methodName, path);"));

            // One assembly site for a rewritten route. The parser builds its own, which
            // is a different thing and is counted separately so this cannot be
            // satisfied by the parser instead.
            Assert.Equal(1, SourceAssert.Count(source, "return new ApiRoute\n"));
            Assert.Equal(1, SourceAssert.Count(source, "routes.Add(new ApiRoute\n"));
        }

        /// <summary>
        /// The write path checks for a conflict twice, before planning and again
        /// after re-reading. Both refusals were written out in full, identical except
        /// for which snapshot's token they reported - and that difference is the
        /// point: a caller retrying with the second one is comparing against what
        /// the API looks like now.
        /// </summary>
        [Fact]
        public void BothVersionConflictChecksReportTheirOwnObservedToken()
        {
            string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ApiIntrospectService.cs")));

            Assert.Equal(1, SourceAssert.Count(source, "private static string VersionConflict("));
            Assert.Equal(2, SourceAssert.Count(source, "return VersionConflict(api, "));

            // The planned-from snapshot, then the freshly-read one. Asserted with the
            // argument spelled out, because swapping them would be a silent change to
            // what a caller is told to retry with.
            Assert.Equal(1, SourceAssert.Count(source, "VersionConflict(api, snapshot.VersionToken, expectedVersion)"));
            Assert.Equal(1, SourceAssert.Count(source, "VersionConflict(api, latestSnapshot.VersionToken, expectedVersion)"));

            // One envelope, and it always says nothing was persisted. Scoped to the
            // envelope: other refusals in this file carry the field too, and counting
            // them would let this pass for the wrong reasons.
            string envelope = SourceAssert.MethodBody(source, "private static string VersionConflict(");
            Assert.Equal(1, SourceAssert.Count(envelope, "[\"persisted\"] = false,"));
            Assert.Equal(1, SourceAssert.Count(envelope, "[\"versionToken\"] = observedVersion,"));
            Assert.Equal(1, SourceAssert.Count(envelope, "[\"expectedVersion\"] = expectedVersion"));
        }

        /// <summary>
        /// A third VersionConflict remains, and it is a different condition: the API
        /// could not be re-read at all, rather than having changed. Its message says
        /// so, and it carries no expectedVersion because there is no comparison to
        /// report against. Folding it in would tell a caller the API changed when the
        /// truth is that it could not be read.
        /// </summary>
        [Fact]
        public void TheReadFailureRefusalStaysItsOwnThing()
        {
            string source = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ApiIntrospectService.cs")));

            Assert.Equal(1, SourceAssert.Count(source, "\"The API could not be re-read before saving; no route was written.\""));
            Assert.Equal(2, SourceAssert.Count(source, "code: \"VersionConflict\""));

            // The shared envelope's message is stated once, so folding the read failure
            // into it would show up here as two.
            Assert.Equal(1, SourceAssert.Count(source, "\"The API changed after the route preview; no route was written.\""));
        }

        private static string SpanAt(ApiIntrospectService.ApiRoute route, int offset, int length)
        {
            int at = route.SourceIndex + offset;
            Assert.InRange(at, 0, ApiSource.Length);
            Assert.InRange(at + length, 0, ApiSource.Length);
            return ApiSource.Substring(at, length);
        }
    }
}
