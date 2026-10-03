using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Reads the tail of a log file without retaining the whole file.
    ///
    /// <para>
    /// Issue #342. <c>ObjectService.ReadLogs</c> opened the log, read every line into a
    /// <see cref="List{T}"/>, filtered that list, and then took the last N. A request for
    /// the last ten lines of a large log therefore allocated the whole log on the
    /// diagnostic path - and that path runs exactly when something has gone wrong and
    /// memory is least welcome.
    /// </para>
    ///
    /// <para>
    /// This reads backwards in fixed-size blocks, keeping at most
    /// <see cref="MaxRetainedLines"/> lines and counting the rest. Retained memory is
    /// bounded by the requested window, not by the file.
    /// </para>
    ///
    /// <para>
    /// The honest cost is that a filter can no longer see lines before the retained
    /// window. That is reported rather than hidden: <see cref="TruncatedFromStart"/>
    /// says how many lines were dropped, so a caller asking "since yesterday" and
    /// getting only the tail can tell that the answer is a tail, not the whole log.
    /// </para>
    /// </summary>
    internal sealed class LogTailReader
    {
        /// <summary>
        /// Hard cap on retained lines. A caller may ask for 2000; an unbounded request
        /// must not turn the tail reader back into the whole-file reader.
        /// </summary>
        internal const int MaxRetainedLines = 2000;

        /// <summary>
        /// Bytes read per backward block. Large enough that a line-dense log does not
        /// degenerate into a read per line, small enough that a tail of a huge file does
        /// not pull megabytes to return a handful of lines.
        /// </summary>
        internal const int BlockSize = 64 * 1024;

        /// <summary>
        /// Issue #370. How far back a filtered scan will read before giving up, so a
        /// no-match search on a multi-gigabyte log cannot turn a diagnostic call into a
        /// full-file read. Generous: the point is to bound the pathological case, not to
        /// make a legitimate deep search fail.
        /// </summary>
        internal const long DefaultMaxScanBytes = 256L * 1024 * 1024;

        private readonly List<string> _lines = new List<string>();

        /// <summary>The retained lines, oldest first.</summary>
        public IReadOnlyList<string> Lines => _lines;

        /// <summary>Every line in the file, including those not retained.</summary>
        public long TotalLines { get; private set; }

        /// <summary>How many lines were dropped from the front of the file.</summary>
        public long TruncatedFromStart => Math.Max(0, TotalLines - _lines.Count);

        /// <summary>
        /// True when the file is larger than the retained window, so any filter applied
        /// afterwards saw only part of the log.
        /// </summary>
        public bool Truncated => TruncatedFromStart > 0;

        /// <summary>
        /// Issue #370. False when the scan stopped on its byte budget rather than because
        /// it reached the start of the file or ran out of relevant lines. A caller must be
        /// able to tell "no match in the log" from "no match in the part we read".
        /// </summary>
        public bool ScanComplete { get; private set; } = true;

        /// <summary>How many bytes the scan actually read.</summary>
        public long ScannedBytes { get; private set; }

        /// <summary>
        /// Reads the last <paramref name="tail"/> lines of <paramref name="path"/>.
        /// </summary>
        /// <param name="path">Log file to read.</param>
        /// <param name="tail">Lines to retain; clamped to at least 1.</param>
        /// <param name="keep">Extra lines retained beyond <paramref name="tail"/>, for
        /// callers whose filters need more context than they return - the crash search
        /// must see the crash marker itself, not just the lines after it.</param>
        public static LogTailReader Read(string path, int tail, int keep = 0)
        {
            if (tail < 1) tail = 1;
            int retain = tail;
            if (keep > 0) retain = tail + keep;
            if (retain > MaxRetainedLines) retain = MaxRetainedLines;
            if (retain < tail) retain = tail; // the cap must never shrink the requested tail

            var result = new LogTailReader();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                result.TotalLines = CountLines(fs);
                result.ReadBackwards(fs, retain);
                result.ScanComplete = true;
            }
            return result;
        }

        /// <summary>
        /// Reads the last <paramref name="tail"/> lines of <paramref name="path"/> that
        /// match <paramref name="isMatch"/>, scanning backwards from the end of the file.
        ///
        /// <para>
        /// Issue #370. <see cref="Read"/> retains a window and the caller filters it
        /// afterwards, so <c>grep</c>, <c>filterCorrelation</c>, <c>objectFilter</c> and
        /// <c>since</c> could only ever match inside that window: asking for the last ten
        /// lines matching a correlation id from ten minutes ago returned nothing on a busy
        /// log. Here the predicate is applied to each complete line as it is decoded, so a
        /// match anywhere in the scanned range is found, while retained memory stays
        /// bounded by the requested number of matches.
        /// </para>
        ///
        /// <para>
        /// <param name="isOutOfScope">Evaluated after the line is offered to
        /// <paramref name="isMatch"/>; returning true from it stops the scan and marks it
        /// complete, because walking backwards means every earlier line is also out of
        /// scope. That is how <c>since=&lt;timestamp&gt;</c> bounds its work and how
        /// <c>since=crash</c> stops at the marker. The line that ends the scan is dropped
        /// unless <paramref name="keepOutOfScopeLine"/> is set: a <c>since=</c> cutoff must
        /// not smuggle in the line it just excluded, while the crash search must keep the
        /// marker itself. <paramref name="maxScanBytes"/> bounds the rest; when it stops the
        /// scan, <see cref="ScanComplete"/> is false.
        /// </para>
        /// </summary>
        public static LogTailReader ReadFiltered(
            string path,
            int tail,
            Func<string, bool> isMatch,
            Func<string, bool> isOutOfScope = null,
            long maxScanBytes = DefaultMaxScanBytes,
            bool keepOutOfScopeLine = false)
        {
            if (tail < 1) tail = 1;
            var result = new LogTailReader();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                result.TotalLines = CountLines(fs);
                if (fs.Length == 0) return result;
                result.ScanComplete = result.ReadBackwardsFiltered(
                    fs, tail, isMatch, isOutOfScope, maxScanBytes, keepOutOfScopeLine);
            }
            return result;
        }

        /// <summary>
        /// Walks backwards applying <paramref name="isMatch"/> per line, keeping the newest
        /// <paramref name="tail"/> matches. Returns false when the byte budget stopped it.
        /// </summary>
        private bool ReadBackwardsFiltered(
            FileStream fs, int tail, Func<string, bool> isMatch, Func<string, bool> isOutOfScope,
            long maxScanBytes, bool keepOutOfScopeLine)
        {
            bool endsWithNewline = LastByteIsNewline(fs);
            var window = new List<byte>(BlockSize * 2);
            byte[] block = new byte[BlockSize];
            long position = fs.Length;
            long budget = maxScanBytes > 0 ? maxScanBytes : long.MaxValue;
            bool firstExtraction = true;

            // Newest-first. Take(line) keeps the newest `tail` matches: once it holds
            // `tail`, dropping the head of a newest-first list discards the oldest match.
            Action<string> keep = line =>
            {
                if (_lines.Count >= tail) return;
                _lines.Add(line);
            };

            while (position > 0)
            {
                int want = (int)Math.Min(BlockSize, position);
                position -= want;
                fs.Seek(position, SeekOrigin.Begin);
                int read = 0;
                while (read < want)
                {
                    int n = fs.Read(block, read, want - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read <= 0) break;
                ScannedBytes += read;
                window.InsertRange(0, block.AsSpan(0, read).ToArray());

                while (true)
                {
                    int lastBreak = window.LastIndexOf((byte)'\n');
                    if (lastBreak < 0) break;
                    int segmentStart = lastBreak + 1;
                    int segmentLength = window.Count - segmentStart;
                    bool emit = !(firstExtraction && endsWithNewline && segmentLength == 0);
                    string line = emit ? Decode(window, segmentStart, segmentLength) : null;
                    firstExtraction = false;
                    window.RemoveRange(lastBreak, window.Count - lastBreak);
                    if (!emit) continue;

                    bool outOfScope = isOutOfScope != null && isOutOfScope(line);
                    if ((isMatch == null || isMatch(line)) && (keepOutOfScopeLine || !outOfScope)) keep(line);
                    if (outOfScope)
                    {
                        _lines.Reverse();
                        return true;
                    }
                    if (_lines.Count >= tail)
                    {
                        _lines.Reverse();
                        return true;
                    }
                }

                if (ScannedBytes >= budget)
                {
                    _lines.Reverse();
                    return false;
                }
            }

            // Reached the head of the file: whatever is buffered is a run of complete
            // lines, not one partial line.
            if (window.Count > 0)
            {
                int segmentStart = 0;
                for (int i = 0; i < window.Count; i++)
                {
                    if (window[i] != (byte)'\n') continue;
                    string line = Decode(window, segmentStart, i - segmentStart);
                    bool outOfScope = isOutOfScope != null && isOutOfScope(line);
                    if ((isMatch == null || isMatch(line)) && (keepOutOfScopeLine || !outOfScope)) keep(line);
                    if (outOfScope)
                    {
                        _lines.Reverse();
                        return true;
                    }
                    segmentStart = i + 1;
                }
                if (segmentStart < window.Count)
                {
                    string line = Decode(window, segmentStart, window.Count - segmentStart);
                    bool lastOutOfScope = isOutOfScope != null && isOutOfScope(line);
                    if ((isMatch == null || isMatch(line)) && (keepOutOfScopeLine || !lastOutOfScope)) keep(line);
                }
            }

            _lines.Reverse();
            return true;
        }

        /// <summary>
        /// Walks the file from the end towards the start, emitting complete lines.
        ///
        /// <para>
        /// The buffer holds one block plus whatever partial line straddles the block
        /// boundary, so peak retained bytes stay bounded regardless of line length or
        /// file size. Lines are collected newest-first and reversed once at the end.
        /// </para>
        /// </summary>
        private void ReadBackwards(FileStream fs, int retain)
        {
            if (fs.Length == 0) return;

            bool endsWithNewline = LastByteIsNewline(fs);
            var window = new List<byte>(BlockSize * 2);
            byte[] block = new byte[BlockSize];
            long position = fs.Length;
            bool firstExtraction = true;

            while (position > 0 && _lines.Count < retain)
            {
                int want = (int)Math.Min(BlockSize, position);
                position -= want;
                fs.Seek(position, SeekOrigin.Begin);
                int read = 0;
                while (read < want)
                {
                    int n = fs.Read(block, read, want - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read <= 0) break;

                // The new block precedes everything already buffered, so it is prepended
                // in forward byte order. The buffer therefore always reads in file order.
                window.InsertRange(0, block.AsSpan(0, read).ToArray());

                // Split complete lines off the end of the buffer, newest first.
                while (_lines.Count < retain)
                {
                    int lastBreak = window.LastIndexOf((byte)'\n');
                    if (lastBreak < 0) break; // no complete line buffered yet

                    int segmentStart = lastBreak + 1;
                    int segmentLength = window.Count - segmentStart;
                    // A file ending in a newline has no final empty line, and StreamReader
                    // does not report one; neither does this.
                    if (!(firstExtraction && endsWithNewline && segmentLength == 0))
                        _lines.Add(Decode(window, segmentStart, segmentLength));

                    firstExtraction = false;
                    // Drop the terminating newline *and* the line's own bytes, not the
                    // text before them: the segment after the newline is the line just
                    // emitted, and leaving it in the buffer would re-emit it on the next
                    // pass as if it were an earlier line.
                    window.RemoveRange(lastBreak, window.Count - lastBreak);
                }
            }

            // Only a buffer that reached offset 0 holds the head of the file, and there
            // it is a run of complete lines, not one partial line. A buffer left over
            // because `retain` filled up is a fragment of a line the caller did not ask
            // for, and appending it would invent a line that does not exist.
            if (position == 0 && window.Count > 0)
            {
                int segmentStart = 0;
                for (int i = 0; i < window.Count && _lines.Count < retain; i++)
                {
                    if (window[i] != (byte)'\n') continue;
                    _lines.Add(Decode(window, segmentStart, i - segmentStart));
                    segmentStart = i + 1;
                }
                // A file whose first line has no newline of its own - which every file that
                // ends with a newline has - leaves exactly this final segment.
                if (segmentStart < window.Count && _lines.Count < retain)
                    _lines.Add(Decode(window, segmentStart, window.Count - segmentStart));
            }

            _lines.Reverse(); // collected newest-first
        }

        private static string Decode(List<byte> buffer, int offset, int count)
        {
            if (count > 0 && buffer[offset + count - 1] == (byte)'\r') count--;
            return Encoding.UTF8.GetString(buffer.ToArray(), offset, count);
        }

        private static bool LastByteIsNewline(FileStream fs)
        {
            if (fs.Length == 0) return false;
            fs.Seek(fs.Length - 1, SeekOrigin.Begin);
            return fs.ReadByte() == (byte)'\n';
        }

        /// <summary>
        /// Counts the file's lines by streaming it, retaining nothing. <c>totalLines</c>
        /// has always meant "how many lines does the log hold", so it cannot be derived
        /// from the retained window without lying about large files.
        /// </summary>
        private static long CountLines(FileStream fs)
        {
            long total = 0;
            bool pendingNewline = true; // offset zero behaves as if preceded by a newline
            byte[] block = new byte[BlockSize];
            fs.Seek(0, SeekOrigin.Begin);
            int read;
            while ((read = fs.Read(block, 0, block.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    if (block[i] != (byte)'\n') { pendingNewline = false; continue; }
                    total++;
                    pendingNewline = true;
                }
            }
            if (!pendingNewline) total++; // a file not ending in a newline has one more line
            return total;
        }
    }
}