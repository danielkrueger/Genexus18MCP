using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using GxMcp.Worker;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// <c>Argv.Quote</c> builds the command line for every <c>git</c> and <c>gh</c>
    /// invocation the server makes, from caller-supplied file, branch and message
    /// text. It had no tests at all: it lived as <c>GithubService.ArgvQuote</c>,
    /// called from five services, and a bug in it is an argument-injection bug
    /// rather than a wrong answer.
    ///
    /// The round-trip tests check the quoting against the real parser rather than
    /// against a reimplementation of it. <c>CommandLineToArgvW</c> is what the CRT
    /// inside <c>git</c> runs, so if the quote and that function disagree, the child
    /// process sees something the caller never wrote.
    ///
    /// Those tests found a real defect, which is recorded rather than silently
    /// fixed - see <see cref="AnEmbeddedDoubleQuoteSplitsTheArgument"/>.
    /// </summary>
    public class ArgvQuotingTests
    {
        private const char BS = '\\';
        private const char DQ = '"';

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("simple")]
        [InlineData("--flag")]
        [InlineData("C:\\Users\\dev\\kb")]
        [InlineData("no spaces here")]
        [InlineData("trailing-backslash\\")]
        [InlineData("a\\")]
        [InlineData("a\\\\")]
        [InlineData("a\\\\\\")]
        public void ASingleQuotedArgumentRoundTripsExactly(string value)
        {
            Assert.Equal(new[] { value ?? string.Empty }, ParseArgvW(Argv.Quote(value)));
        }

        [Theory]
        [InlineData("with space")]
        [InlineData("tab\there")]
        [InlineData("line\nbreak")]
        [InlineData("vertical\vtab")]
        [InlineData("a b c")]
        public void ArgumentsWithSeparatorsRoundTripExactly(string value)
        {
            Assert.Equal(new[] { value }, ParseArgvW(Argv.Quote(value)));
        }

        [Fact]
        public void AnEmbeddedDoubleQuoteSplitsTheArgument()
        {
            // KNOWN DEFECT, pre-existing, carried over unchanged by the move to
            // Helpers/Argv.cs. Measured against CommandLineToArgvW - the parser the
            // CRT inside git/gh actually runs - this does not round-trip:
            //
            //   a b      -> 1 token                OK
            //   a b\     -> 1 token, "a b\\"       the trailing backslash doubles
            //   a\"b     -> 2 tokens               the quote is not escaped
            //   a b\"c d -> 3 tokens
            //
            // So a caller-supplied commit message or branch name containing a
            // double quote becomes extra git arguments. This is the quoting
            // primitive every git and gh invocation goes through, called from five
            // services, and it had no test at all before this file.
            //
            // NOT fixed here: choosing a correct encoding changes what command
            // lines the server builds and needs its own verification, which is not
            // part of relocating the primitive. Pinned so the behaviour is
            // recorded rather than assumed.
            string[] fromEmbeddedQuote = ParseArgvW(Argv.Quote("a" + BS + DQ + "b"));
            Assert.Equal(2, fromEmbeddedQuote.Length);
            Assert.Equal("a" + BS + BS + BS, fromEmbeddedQuote[0]);
            Assert.Equal("b", fromEmbeddedQuote[1]);

            Assert.Equal(3, ParseArgvW(Argv.Quote("a b" + BS + DQ + "c d")).Length);

            // And a trailing backslash after a separator survives doubled.
            Assert.Equal("a b" + BS + BS, ParseArgvW(Argv.Quote("a b" + BS))[0]);
        }

        [Fact]
        public void TheQuotingShapeItselfIsUnchangedByThisMove()
        {
            // Pinned so the relocation can be reviewed as a pure move: whatever the
            // gaps above, these are the exact bytes GithubService.ArgvQuote emitted.
            Assert.Equal("a", Argv.Quote("a"));
            Assert.Equal(DQ + "a b" + DQ, Argv.Quote("a b"));
            Assert.Equal(DQ + "a b" + BS + BS + DQ, Argv.Quote("a b" + BS));
            Assert.Equal(DQ + "a" + BS + BS + BS + DQ + "b" + DQ, Argv.Quote("a" + BS + DQ + "b"));
        }

        [Fact]
        public void JoinPreservesArgumentCountAndOrder()
        {
            var args = new[] { "--repo", "owner/name", "--message", "fix the parser", "path with space" };

            Assert.Equal(args, ParseArgvW(Argv.Join(args)));
        }

        [Fact]
        public void JoinSeparatesWithExactlyOneSpace()
        {
            // Two separators would produce an empty argument on the C runtime's
            // reading of an unquoted run; a caller counting tokens would be off.
            Assert.Equal("a b", Argv.Join(new[] { "a", "b" }));
            Assert.Equal("a", Argv.Join(new[] { "a" }));
            Assert.Equal(string.Empty, Argv.Join(new string[0]));
            Assert.Equal(string.Empty, Argv.Join(null));
        }

        [Fact]
        public void AnUnquotedArgumentIsHandedBackUntouched()
        {
            // Nothing the runtime would reinterpret, so adding quotes would mean
            // escaping quotes that then have to be stripped again.
            foreach (string plain in new[] { "simple", "--flag=1", "a-b-c", "C:\\tmp\\x", "trailing-" })
                Assert.Equal(plain, Argv.Quote(plain));
        }

        [Fact]
        public void ATrailingBackslashRunIsDoubledInsideQuotes()
        {
            // The textual shape of the intent: once a value is quoted, the run
            // before the closing quote is escaped rather than left to consume it.
            // Note a value with no separator is never quoted at all, so it is not
            // doubled - see AnUnquotedArgumentIsHandedBackUntouched.
            Assert.Equal(DQ + "a value" + BS + BS + DQ, Argv.Quote("a value" + BS));
            Assert.Equal(DQ + "a b" + BS + BS + DQ, Argv.Quote("a b" + BS));
        }

        [Fact]
        public void BackslashesNotPrecedingAQuoteAreLeftAlone()
        {
            // Only a run that precedes a quote is special; doubling every backslash
            // would corrupt ordinary Windows paths.
            Assert.Equal(
                DQ + "a C:" + BS + "dir" + BS + "file.txt here" + DQ,
                Argv.Quote("a C:" + BS + "dir" + BS + "file.txt here"));
        }

        [Fact]
        public void TheQuotingPrimitiveLivesInOnePlaceAndEveryCallerUsesIt()
        {
            // It used to be GithubService.ArgvQuote, reached from five services. A
            // general primitive parked in one service's namespace is a primitive the
            // next service reimplements instead of calling.
            foreach (string file in new[]
            {
                "GithubService.cs", "BlameService.cs", "CrossBrowserService.cs",
                "GeneratedDiffService.cs", "TimeTravelService.cs",
            })
            {
                Assert.DoesNotContain("ArgvQuote", RepoSource.Read("src", "GxMcp.Worker", "Services", file));
            }

            string helper = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "Argv.cs");
            Assert.Equal(1, SourceAssert.Count(helper, "internal static string Quote(string arg)"));
            Assert.Equal(1, SourceAssert.Count(helper, "internal static string Join("));

            foreach (string src in RepoSource.CsFilesIn("src", "GxMcp.Worker", "Services"))
                Assert.DoesNotContain("internal static string ArgvQuote", File.ReadAllText(src));
        }

        [Fact]
        public void EveryPreviousCallerNowReachesTheHelper()
        {
            // Each of the five reaches it at least once. Asserted per file rather
            // than as a total, because how many calls a service makes is its own
            // business - only "none of them is left behind" is the invariant.
            foreach (string file in new[] { "GithubService.cs", "BlameService.cs", "CrossBrowserService.cs", "GeneratedDiffService.cs", "TimeTravelService.cs" })
                Assert.True(SourceAssert.Count(RepoSource.Read("src", "GxMcp.Worker", "Services", file), "Argv.") >= 1, file + " does not use Argv");
        }

        /// <summary>
        /// Splits a command line the way the C runtime will, via the same
        /// <c>CommandLineToArgvW</c> that runs inside the child process.
        /// </summary>
        private static string[] ParseArgvW(string commandLine)
        {
            int argc;
            IntPtr argv = CommandLineToArgvW(commandLine ?? string.Empty, out argc);
            if (argv == IntPtr.Zero)
                throw new InvalidOperationException("CommandLineToArgvW rejected: " + commandLine);

            try
            {
                var parsed = new string[argc];
                for (int i = 0; i < argc; i++)
                    parsed[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                return parsed;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string cmdLine, out int argc);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr handle);

    }
}
