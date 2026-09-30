using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Reading a property's current value and reading the declared type used to
    /// coerce a new value into each carried their own copy of the same
    /// case-insensitive resolution over an SDK property bag, now
    /// <c>ResolvePropertyEntry</c>.
    ///
    /// The two are not independent lookups. A write reads the existing value with
    /// one and coerces the new value against the declared type of the other, and
    /// both are given the same property name. If they resolved different entries -
    /// say one found the property only by its case-insensitive scan - a write would
    /// read one property's current value while coercing against another property's
    /// type. That fails as a silently mis-set value, not an error, so the
    /// single-source property is asserted structurally here.
    /// </summary>
    public class PropertyEntryResolutionTests
    {
        [Fact]
        public void BothCallers_ResolveThePropertyEntryThroughTheSharedHelper()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs");

            Assert.Equal(1, CountOccurrences(src, "private static object ResolvePropertyEntry(dynamic container, string propName)"));
            Assert.Equal(2, CountOccurrences(src, "ResolvePropertyEntry(container, propName)"));

            // And neither caller re-implements the case-insensitive fallback: the
            // direct bag lookup and the scan that backs it each appear once, in
            // the helper.
            Assert.Equal(1, CountOccurrences(src, "container.Properties?[propName]"));
            Assert.Equal(1, CountOccurrences(src, "StringComparison.OrdinalIgnoreCase)) return p;"));
        }

        [Fact]
        public void BothNamedCallers_ReachTheHelper()
        {
            // Not just "the file contains two calls" - the two callers are the
            // reader of a property's current value and the coercer that decides the
            // type a new value is written as. Both must go through the helper, or a
            // write can read one entry's value and coerce against another's type.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs");

            foreach (string caller in new[] { "TryReadPropertyString", "TryCoercePropertyValue" })
            {
                int at = src.IndexOf("private static " + (caller.StartsWith("TryRead") ? "string " : "bool ") + caller + "(", StringComparison.Ordinal);
                Assert.True(at > 0, caller + " not found");

                int next = src.IndexOf("        private static ", at + 10, StringComparison.Ordinal);
                string body = next < 0 ? src.Substring(at) : src.Substring(at, next - at);

                Assert.Contains("ResolvePropertyEntry(container, propName)", body);
                Assert.DoesNotContain("container.Properties?[propName]", body);
                Assert.DoesNotContain("foreach (dynamic p in container.Properties)", body);
            }
        }

        [Fact]
        public void TheCaseInsensitiveFallback_IsTheOnlyNameMatchInTheFile()
        {
            // A third property-name comparison introduced with its own fallback is
            // the drift this consolidation exists to prevent.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs");

            Assert.Equal(1, CountOccurrences(src, "foreach (dynamic p in container.Properties)"));
            Assert.Equal(1, CountOccurrences(src, "try { n = (string)p.Name; } catch { }"));
        }

        [Fact]
        public void TheHelperIsReachableFromBothSites_AndTheCoercerKeepsItsGuard()
        {
            // TryCoercePropertyValue guards every SDK access because a property bag
            // on a partially-loaded object throws rather than returning null; the
            // shared helper must not have cost it that guard.
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "PropertyService.cs");

            int helper = src.IndexOf("private static object ResolvePropertyEntry(", StringComparison.Ordinal);
            Assert.True(helper > 0, "ResolvePropertyEntry not found");

            int helperEnd = src.IndexOf("private static string TryReadPropertyString(", helper, StringComparison.Ordinal);
            string helperBody = src.Substring(helper, helperEnd - helper);

            Assert.Contains("catch { }", helperBody);
            Assert.Contains("return null;", helperBody);
            Assert.Contains("if (existing != null) return existing;", helperBody);
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
