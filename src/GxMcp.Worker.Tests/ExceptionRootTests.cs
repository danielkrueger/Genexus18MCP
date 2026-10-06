using System;
using System.Reflection;
using System.Globalization;
using System.Threading;
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

        [Theory]
        [InlineData("en-US")]
        [InlineData("pt-BR")]
        public void FailureTraceIsIndependentOfUICulture(string culture)
        {
            var thread = Thread.CurrentThread;
            var previousCulture = thread.CurrentCulture;
            var previousUI = thread.CurrentUICulture;
            try
            {
                thread.CurrentCulture = CultureInfo.InvariantCulture;
                thread.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                string trace = ExceptionRoot.FailureTrace(new TargetInvocationException(Thrown()));
                Assert.Contains(nameof(Thrown), trace);
                Assert.DoesNotContain("(", trace);
                Assert.DoesNotContain(".cs", trace);
            }
            finally
            {
                thread.CurrentCulture = previousCulture;
                thread.CurrentUICulture = previousUI;
            }
        }

        private sealed class TextOnlyException : Exception
        {
            public override string StackTrace => "   em Fictional.Report.Save(System.String value) em C:\\private\\Report.cs:linha 3\n--- remote boundary ---\n   at Fictional.Report.Load()";
        }

        [Fact]
        public void TextOnlyTraceRetainsQualifiedMethodsWithoutPathsOrArguments()
        {
            Assert.Equal("Fictional.Report.Save <- Fictional.Report.Load", ExceptionRoot.FailureTrace(new TextOnlyException()));
            Assert.Null(ExceptionRoot.FailureTrace(new Exception()));
            Assert.Null(ExceptionRoot.FailureTrace(null));
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
