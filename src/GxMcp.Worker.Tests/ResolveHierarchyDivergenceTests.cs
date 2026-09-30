using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Two methods named <c>ResolveHierarchy</c> walk the same <c>Parent</c> chain line
    /// for line, and the duplication is deliberate. This pins why, because the
    /// similarity is the kind that invites a merge and the merge would be wrong.
    ///
    /// Both agree on the walk: skip what is not a Module or Folder, stop at the
    /// DesignModel, refuse to loop when a parent's Guid matches the object's, and take
    /// the first container's name as the parent. They are even written the same way -
    /// same name, same locals, same cycle guard - down to a difference in shape with no
    /// behavioural content: the index copy hoists the container test into a local and
    /// skips ahead, the list copy falls through to the same advance.
    ///
    /// They part at the end, and the part is the reason. When the chain yields nothing,
    /// <c>IndexCacheService</c> promotes the module into the path, because the result is
    /// cached as the index's own identity for the object and that has to stay stable
    /// across a cache rebuild. <c>ListService</c> must not, because its result is
    /// reported as <c>item["parentPath"]</c> and a module is not where the object lives.
    /// A shared helper would pick one and make the other consumer wrong.
    ///
    /// Source-level, and honestly so: the walk gates on <c>is Module</c> / <c>is
    /// Folder</c>, and no test in this project builds an SDK Module or Folder, so the
    /// half of the walk that records a container name is not behaviourally covered here
    /// and cannot be without a live KB. What is pinned below is the divergence - the
    /// one line that separates the two - not the walk itself.
    /// </summary>
    public class ResolveHierarchyDivergenceTests
    {
        // The whole difference between the two implementations.
        private const string ModulePromotion =
            "if (parentSegments.Count == 0 && !string.IsNullOrWhiteSpace(moduleName))";

        private const string IndexWalk = "private (string ParentName, string ParentPath, string Path, string ModuleName) ResolveHierarchy(";
        private const string ListWalk = "private HierarchyInfo ResolveHierarchy(";

        private static string Index() =>
            RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "IndexCacheService.cs"));

        private static string List() =>
            RepoSource.WithoutComments(RepoSource.Read("src", "GxMcp.Worker", "Services", "ListService.cs"));

        [Fact]
        public void BothResolversExistAndAreDistinctMethods()
        {
            Assert.Equal(1, SourceAssert.Count(Index(), IndexWalk));
            Assert.Equal(1, SourceAssert.Count(List(), ListWalk));

            // Same walk, not a shared one: each file defines its own and neither calls
            // the other, so a merge has to be a deliberate edit rather than an accident
            // of one already calling through.
            Assert.Equal(0, SourceAssert.Count(Index(), ListWalk));
            Assert.Equal(0, SourceAssert.Count(List(), IndexWalk));
        }

        [Fact]
        public void TheIndexPromotesTheModuleAndTheListDoesNot()
        {
            Assert.Equal(1, SourceAssert.Count(Index(), ModulePromotion));
            Assert.Equal(0, SourceAssert.Count(List(), ModulePromotion));
        }

        /// <summary>
        /// Both promote the module into their own <c>moduleName</c> when the chain gives
        /// them nothing, so the two differ only in whether that name also becomes the
        /// path.
        /// </summary>
        [Fact]
        public void BothKeepTheModuleFallbackAndOnlyTheIndexPromotesIt()
        {
            const string Fallback = "obj.Module.Guid != obj.Guid";

            Assert.Equal(1, SourceAssert.Count(Index(), Fallback));
            Assert.Equal(1, SourceAssert.Count(List(), Fallback));

            // The list copy must still report the module even though it refuses to put
            // it in the path, so the fallback surviving is not the same as the
            // promotion being copied across.
            Assert.Equal(1, SourceAssert.Count(List(), "ModuleName = moduleName ?? string.Empty,"));
        }
    }
}
