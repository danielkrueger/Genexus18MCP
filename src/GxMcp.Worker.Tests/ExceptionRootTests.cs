using System;
using System.Reflection;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #419: the real SDK failure sits under TargetInvocationException.
    public class ExceptionRootTests
    {
        private static Exception Thrown() { try { throw new InvalidCastException("Unable to cast String to KBObject"); } catch (Exception e) { return e; } }

        [Fact]
        public void NestedReflectionAndAggregateWrappersUnwrapToTheOriginal()
        {
            var original = Thrown();
            var wrapped = new TargetInvocationException(new AggregateException(new TargetInvocationException(original)));
            Assert.Same(original, ExceptionRoot.Unwrap(wrapped));
            Assert.Equal("Unable to cast String to KBObject", ExceptionRoot.Message(wrapped));
        }

        [Fact]
        public void PlainExceptionIsItsOwnRoot()
        {
            var ex = new InvalidOperationException("x");
            Assert.Same(ex, ExceptionRoot.Unwrap(ex));
        }

        [Fact]
        public void FailureTraceListsMethodNamesOnly()
        {
            string trace = ExceptionRoot.FailureTrace(new TargetInvocationException(Thrown()));
            Assert.Contains(nameof(Thrown), trace);
            Assert.DoesNotContain("(", trace);
            Assert.DoesNotContain(".cs", trace);
        }
    }
}
