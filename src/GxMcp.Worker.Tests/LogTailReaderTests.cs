using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #342, part two: a request for the last N lines of a log retained the whole
    /// file. A ten-line tail of a large log allocated the entire log - on the diagnostic
    /// route an agent reaches for precisely when something has gone wrong and memory is
    /// least welcome.
    ///
    /// <para>
    /// These assert the reader directly, including the boundary cases a backward reader
    /// gets wrong: a file with no trailing newline, a file ending exactly on a block
    /// boundary, CRLF, non-ASCII, and a single line longer than one block. A backward
    /// reader that mishandles any of these silently returns the wrong lines, which is
    /// worse than failing.
    /// </para>
    /// </summary>
    public class LogTailReaderTests : IDisposable
    {
        private readonly string _dir;

        public LogTailReaderTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gxmcp-logtail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private string Write(string name, params string[] lines)
        {
            string path = Path.Combine(_dir, name);
            // No trailing newline: this is the case a naive "split on \n" reader gets
            // wrong by reporting a phantom final empty line.
            File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false));
            return path;
        }

        private static string[] Expected(params string[] lines) => lines;

        [Fact]
        public void The_Retained_Cap_Is_Bounded()
        {
            // The cap is what stops "tail 10 lines" from degenerating back into the
            // whole-file read, so it must not be a value a caller can exceed.
            Assert.InRange(LogTailReader.MaxRetainedLines, 1, 100_000);
            Assert.True(LogTailReader.BlockSize > 0);
        }

        [Fact]
        public void A_Tail_Returns_The_Last_Lines_And_Counts_The_Whole_File()
        {
            string path = Write("basic.txt", "l1", "l2", "l3", "l4", "l5");

            var r = LogTailReader.Read(path, tail: 2);

            Assert.Equal(Expected("l4", "l5"), r.Lines);
            // totalLines must still mean "how many lines the log holds", which a bounded
            // reader can only answer by counting what it did not retain.
            Assert.Equal(5, r.TotalLines);
            Assert.True(r.Truncated);
            Assert.Equal(3, r.TruncatedFromStart);
        }

        [Fact]
        public void A_File_Smaller_Than_The_Window_Is_Not_Reported_As_Truncated()
        {
            // Over-reporting truncation would push every caller to raise `lines` for a
            // log that was fully read.
            string path = Write("small.txt", "l1", "l2", "l3");

            var r = LogTailReader.Read(path, tail: 100);

            Assert.Equal(Expected("l1", "l2", "l3"), r.Lines);
            Assert.Equal(3, r.TotalLines);
            Assert.False(r.Truncated);
            Assert.Equal(0, r.TruncatedFromStart);
        }

        [Fact]
        public void A_File_Without_A_Trailing_Newline_Loses_No_Last_Line()
        {
            string path = Write("nonewline.txt", "a", "b", "c");

            var r = LogTailReader.Read(path, tail: 10);

            Assert.Equal(Expected("a", "b", "c"), r.Lines);
            Assert.Equal(3, r.TotalLines);
        }

        [Fact]
        public void A_File_With_A_Trailing_Newline_Reports_No_Phantom_Last_Line()
        {
            // The mirror of the previous case, and the one a split-on-newline reader
            // gets wrong: "a\nb\n" is two lines, not three.
            string path = Path.Combine(_dir, "trailing.txt");
            File.WriteAllText(path, "a\nb\n", new UTF8Encoding(false));

            var r = LogTailReader.Read(path, tail: 10);

            Assert.Equal(Expected("a", "b"), r.Lines);
            Assert.Equal(2, r.TotalLines);
        }

        [Fact]
        public void An_Empty_File_Reads_As_Empty()
        {
            string path = Path.Combine(_dir, "empty.txt");
            File.WriteAllText(path, "", new UTF8Encoding(false));

            var r = LogTailReader.Read(path, tail: 10);

            Assert.Empty(r.Lines);
            Assert.Equal(0, r.TotalLines);
            Assert.False(r.Truncated);
        }

        [Fact]
        public void Crlf_Lines_Are_Returned_Without_Their_Carriage_Returns()
        {
            // The reader decodes byte runs; a raw \r would otherwise end up inside the
            // returned text and break every caller's filter.
            string path = Path.Combine(_dir, "crlf.txt");
            File.WriteAllText(path, "alpha\r\nbeta\r\ngamma\r\n", new UTF8Encoding(false));

            var r = LogTailReader.Read(path, tail: 10);

            Assert.Equal(Expected("alpha", "beta", "gamma"), r.Lines);
            Assert.Equal(3, r.TotalLines);
        }

        [Fact]
        public void Non_Ascii_Content_Survives_Reversing()
        {
            // Reversing bytes before decoding is the only order that keeps multi-byte
            // UTF-8 sequences intact; decoding first, or reversing after, corrupts them.
            string path = Write("utf8.txt", "ação", "über", "日本語");
            string expectedA = "ação", expectedB = "über", expectedC = "日本語";

            var r = LogTailReader.Read(path, tail: 10);

            Assert.Equal(new[] { expectedA, expectedB, expectedC }, r.Lines);
        }

        [Fact]
        public void A_Line_Longer_Than_One_Block_Is_Returned_Whole()
        {
            // A backward reader that assumes a line fits in its buffer returns a fragment.
            // This is the case that decides whether the block size is a real constraint.
            string big = new string('x', LogTailReader.BlockSize * 3 + 17);
            string path = Write("bigline.txt", "first", big, "last");

            var r = LogTailReader.Read(path, tail: 3);

            Assert.Equal(3, r.Lines.Count);
            Assert.Equal("last", r.Lines[2]);
            Assert.Equal(big, r.Lines[1]);
            Assert.Equal("first", r.Lines[0]);
        }

        [Fact]
        public void A_File_Larger_Than_One_Block_Is_Reassembled_Across_The_Boundary()
        {
            // Lines straddle the block boundary, which is the normal case for a real log
            // and the one a naive fixed-block reader gets wrong.
            var lines = Enumerable.Range(0, 5000).Select(i => "line-" + i + "-" + new string('y', 20)).ToArray();
            string path = Write("multiblock.txt", lines);

            var r = LogTailReader.Read(path, tail: 100);

            Assert.Equal(100, r.Lines.Count);
            Assert.Equal(lines[4900], r.Lines[0]);
            Assert.Equal(lines[4999], r.Lines[99]);
            Assert.Equal(5000, r.TotalLines);
        }

        [Fact]
        public void Retention_Is_Bounded_Regardless_Of_File_Size()
        {
            // The memory property itself. A 200k-line file must not produce 200k
            // retained strings; this is the assertion that makes the fix's claim
            // checkable rather than asserted in a comment.
            var lines = Enumerable.Range(0, 200_000).Select(i => "line-" + i).ToArray();
            string path = Write("huge.txt", lines);

            var r = LogTailReader.Read(path, tail: 50);

            Assert.Equal(50, r.Lines.Count);
            Assert.Equal(200_000, r.TotalLines);
            Assert.Equal(199_950, r.TruncatedFromStart);
        }

        [Fact]
        public void A_Requested_Tail_Is_Never_Shrunk_By_The_Keep_Context_Cap()
        {
            // `keep` exists so a filter can see more than it returns. If the hard cap
            // then shrank the caller's requested tail, a request for 2000 lines would
            // silently come back with fewer.
            var lines = Enumerable.Range(0, 400).Select(i => "line-" + i).ToArray();
            string path = Write("keep.txt", lines);

            var r = LogTailReader.Read(path, tail: LogTailReader.MaxRetainedLines,
                keep: LogTailReader.MaxRetainedLines);

            Assert.True(r.Lines.Count <= LogTailReader.MaxRetainedLines);
            Assert.Equal(400, r.Lines.Count); // the file is smaller than the cap
        }

        [Fact]
        public void A_Non_Positive_Tail_Still_Returns_One_Line()
        {
            string path = Write("one.txt", "only");

            var r = LogTailReader.Read(path, tail: 0);

            Assert.Single(r.Lines);
            Assert.Equal("only", r.Lines[0]);
        }

        [Fact]
        public void Keep_Context_Extends_The_Window_Beyond_The_Requested_Tail()
        {
            var lines = Enumerable.Range(0, 100).Select(i => "line-" + i).ToArray();
            var withCrash = lines.Concat(new[] { "[ERROR] boom", "after-1", "after-2" }).ToArray();
            string path = Write("crash2.txt", withCrash);

            var r = LogTailReader.Read(path, tail: 2, keep: 50);

            Assert.Equal(52, r.Lines.Count);
            Assert.Contains("[ERROR] boom", r.Lines);
            Assert.Equal("after-2", r.Lines[r.Lines.Count - 1]);
        }
    }
}