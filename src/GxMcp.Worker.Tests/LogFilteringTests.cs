using System;
using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Item 32 — genexus_logs filter parameters: target, tail/lines, since (ISO timestamp).
    /// Uses the logPathOverride test seam in ObjectService.ReadLogs to avoid dependency on
    /// Assembly.GetEntryAssembly() path resolution.
    /// </summary>
    public class LogFilteringTests : IDisposable
    {
        private readonly string _logPath;
        private readonly ObjectService _svc;

        public LogFilteringTests()
        {
            _logPath = Path.Combine(Path.GetTempPath(), $"gxmcp-log-test-{Guid.NewGuid():N}.log");

            // Build a minimal ObjectService (ReadLogs does not use KB/build).
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            _svc = new ObjectService(kb, build);
        }

        public void Dispose()
        {
            try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { }
        }

        private void WriteLog(string[] lines) => File.WriteAllLines(_logPath, lines);

        private string CallReadLogs(int lines = 100, string filterCorrelation = null,
            string grepPattern = null, string sinceMode = null, string objectFilter = null)
            => _svc.ReadLogs(lines, filterCorrelation, grepPattern, sinceMode, objectFilter, _logPath);

        private static JObject ParseResult(string json) => JObject.Parse(json);

        // -----------------------------------------------------------------------
        // tail (configurable, default 100)
        // -----------------------------------------------------------------------

        [Fact]
        public void ReadLogs_Tail_ReturnsLastNLines()
        {
            var allLines = new string[50];
            for (int i = 0; i < 50; i++)
                allLines[i] = $"[2026-05-22 10:00:{i:D2}.000] [INFO] Line {i}";
            WriteLog(allLines);

            var result = ParseResult(CallReadLogs(lines: 5));
            Assert.Equal("ok", result["status"]?.ToString());
            var lines = result["result"]?["lines"]?.ToString().Split('\n');
            Assert.Equal(5, lines?.Length);
            Assert.Contains("Line 49", lines![lines.Length - 1]);
        }

        [Fact]
        public void ReadLogs_DefaultTail_Returns100Lines_WhenLogHasMore()
        {
            var allLines = new string[200];
            for (int i = 0; i < 200; i++)
                allLines[i] = $"[2026-05-22 10:00:00.000] [INFO] Line {i}";
            WriteLog(allLines);

            var result = ParseResult(CallReadLogs(lines: 0)); // 0 → default 100
            Assert.Equal("ok", result["status"]?.ToString());
            var lines = result["result"]?["lines"]?.ToString().Split('\n');
            Assert.Equal(100, lines?.Length);
        }

        // -----------------------------------------------------------------------
        // target (object-name filter)
        // -----------------------------------------------------------------------

        [Fact]
        public void ReadLogs_ObjectFilter_OnlyReturnsLinesContainingObjectName()
        {
            WriteLog(new[]
            {
                "[2026-05-22 10:00:01.000] [INFO] Processing MyProc start",
                "[2026-05-22 10:00:02.000] [INFO] Processing OtherProc start",
                "[2026-05-22 10:00:03.000] [INFO] MyProc completed",
                "[2026-05-22 10:00:04.000] [INFO] OtherProc completed",
            });

            var result = ParseResult(CallReadLogs(objectFilter: "MyProc"));
            Assert.Equal("ok", result["status"]?.ToString());
            var lines = result["result"]?["lines"]?.ToString().Split('\n');
            Assert.Equal(2, lines?.Length);
            Assert.All(lines!, l => Assert.Contains("MyProc", l));
        }

        [Fact]
        public void ReadLogs_ObjectFilter_IsCaseInsensitive()
        {
            WriteLog(new[]
            {
                "[2026-05-22 10:00:01.000] [INFO] Loading myproc",
                "[2026-05-22 10:00:02.000] [INFO] Unrelated line",
            });

            var result = ParseResult(CallReadLogs(objectFilter: "MYPROC"));
            Assert.Equal("ok", result["status"]?.ToString());
            var lines = result["result"]?["lines"]?.ToString().Split('\n');
            Assert.Equal(1, lines?.Length);
        }

        // -----------------------------------------------------------------------
        // since (ISO timestamp filter)
        // -----------------------------------------------------------------------

        [Fact]
        public void ReadLogs_SinceIso_FiltersLinesBeforeTimestamp()
        {
            WriteLog(new[]
            {
                "[2026-05-22 09:00:00.000] [INFO] Early line",
                "[2026-05-22 10:00:00.000] [INFO] Boundary line",
                "[2026-05-22 11:00:00.000] [INFO] Late line",
            });

            // Request lines >= 2026-05-22T10:00:00 (local format matches log prefix).
            var result = ParseResult(CallReadLogs(sinceMode: "2026-05-22T10:00:00"));
            Assert.Equal("ok", result["status"]?.ToString());
            var lines = result["result"]?["lines"]?.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.NotNull(lines);
            Assert.DoesNotContain(lines!, l => l.Contains("Early line"));
            Assert.Contains(lines!, l => l.Contains("Boundary line"));
            Assert.Contains(lines!, l => l.Contains("Late line"));
        }

        [Fact]
        public void ReadLogs_SinceIso_KeepsUnparseableLines()
        {
            WriteLog(new[]
            {
                "[2026-05-22 09:00:00.000] [INFO] Old line",
                "stack trace line without timestamp",
                "[2026-05-22 11:00:00.000] [INFO] New line",
            });

            var result = ParseResult(CallReadLogs(sinceMode: "2026-05-22T10:00:00"));
            Assert.Equal("ok", result["status"]?.ToString());
            var lines = result["result"]?["lines"]?.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.NotNull(lines);
            // Unparseable lines (no timestamp) are kept defensively.
            Assert.Contains(lines!, l => l.Contains("stack trace line without timestamp"));
        }

        // -----------------------------------------------------------------------
        // logPath in response (Item 32: surface where logs live)
        // -----------------------------------------------------------------------

        [Fact]
        public void ReadLogs_ResponseIncludesLogPath()
        {
            WriteLog(new[] { "[2026-05-22 10:00:00.000] [INFO] test" });
            var result = ParseResult(CallReadLogs());
            Assert.Equal("ok", result["status"]?.ToString());
            Assert.NotNull(result["result"]?["logPath"]?.ToString());
            Assert.NotNull(result["result"]?["logDir"]?.ToString());
        }

        // -----------------------------------------------------------------------
        // plan 068: bounded regex match timeout on grepPattern
        // -----------------------------------------------------------------------

        [Fact]
        public void ReadLogs_PathologicalGrepPattern_DegradesToSubstringInsteadOfHanging()
        {
            // A catastrophic-backtracking pattern against a run of 'a' + a non-matching
            // tail would hang the STA thread under .NET Framework's infinite default
            // match timeout. The 2s bounded timeout forces the substring fallback inside
            // the call. Pattern avoids possessive quantifiers ("(a+)++$" is only
            // catastrophic on .NET Framework) AND identical alternation branches
            // ("(a|a)+$" is quadratic, not exponential) — "(a|aa)+$" partitions the run
            // into 1-or-2-char chunks, which is pathological on every regex engine.
            var longLine = new string('a', 20000) + "b";
            WriteLog(new[] { $"[2026-05-22 10:00:00.000] [INFO] {longLine}" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = ParseResult(CallReadLogs(grepPattern: "(a|aa)+$"));
            sw.Stop();

            Assert.Equal("ok", result["status"]?.ToString());
            // 2s per-match timeout + small overhead; without the guard this would
            // never return on the single STA thread.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"grep filter took {sw.Elapsed}");
            var lines = result["result"]?["lines"]?.ToString()
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.NotNull(lines);
            // Substring fallback has no literal "(a|aa)+$" in the line → filtered out.
            Assert.Empty(lines);
        }

        [Fact]
        public void ReadLogs_SimpleGrepPattern_StillFiltersRegexNormally()
        {
            // Guard: the bounded timeout must not change behaviour for well-formed
            // patterns (the common case).
            WriteLog(new[]
            {
                "[2026-05-22 10:00:01.000] [INFO] Processing MyProc start",
                "[2026-05-22 10:00:02.000] [INFO] Processing OtherProc start",
            });

            var result = ParseResult(CallReadLogs(grepPattern: "^.*MyProc.*$"));
            Assert.Equal("ok", result["status"]?.ToString());
            var lines = result["result"]?["lines"]?.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.NotNull(lines);
            Assert.Single(lines);
            Assert.Contains("MyProc", lines![0]);
        }

        // -----------------------------------------------------------------------
        // Issue #370: the bounded tail read had turned every filter into
        // "the last N lines that match", so a correlation id from a few minutes
        // ago was unfindable unless the caller guessed a large enough `lines`.
        // -----------------------------------------------------------------------

        /// <summary>A 200,000-line log where the marker appears only near the start.</summary>
        private string WriteMarkerNearTheStart(int total = 200_000, int markerFrom = 1000, int markerCount = 6)
        {
            var lines = new List<string>(total);
            for (int i = 0; i < total; i++)
            {
                bool isMarker = i >= markerFrom && i < markerFrom + markerCount;
                lines.Add(isMarker
                    ? $"[2026-05-22 10:00:00.000] [INFO] corr-0001 marker line {i}"
                    : $"[2026-05-22 10:00:00.000] [INFO] filler line {i}");
            }
            File.WriteAllLines(_logPath, lines);
            return _logPath;
        }

        [Fact]
        public void A_Correlation_Filter_Finds_A_Marker_Far_Before_The_Tail()
        {
            WriteMarkerNearTheStart();

            var result = ParseResult(CallReadLogs(lines: 10, filterCorrelation: "corr-0001"));

            Assert.Equal("ok", result["status"]?.ToString());
            var lines = result["result"]?["lines"]?.ToString()
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // The defect returned nothing here: the retained window was the last 10
            // lines, none of which contain the marker.
            Assert.NotNull(lines);
            Assert.Equal(6, lines!.Length);
            Assert.All(lines, l => Assert.Contains("corr-0001", l));
            Assert.True(result["result"]!["scanComplete"]!.Value<bool>(),
                "the scan reached the start of the file, so it is a complete answer");
        }

        [Fact]
        public void A_Grep_Filter_Finds_A_Match_Far_Before_The_Tail()
        {
            WriteMarkerNearTheStart();

            var result = ParseResult(CallReadLogs(lines: 10, grepPattern: "marker line 1003"));
            var lines = result["result"]?["lines"]?.ToString()
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            Assert.NotNull(lines);
            Assert.Single(lines!);
            Assert.Contains("marker line 1003", lines![0]);
        }

        [Fact]
        public void An_Object_Filter_Finds_A_Match_Far_Before_The_Tail()
        {
            WriteMarkerNearTheStart();

            var result = ParseResult(CallReadLogs(lines: 10, objectFilter: "corr-0001"));
            var lines = result["result"]?["lines"]?.ToString()
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            Assert.NotNull(lines);
            Assert.Equal(6, lines!.Length);
        }

        [Fact]
        public void A_Since_Filter_Finds_Lines_Far_Before_The_Tail()
        {
            var lines = new List<string>();
            // 500 old lines, then 500 lines inside the window, all past the tail of 10.
            for (int i = 0; i < 500; i++) lines.Add($"[2026-05-20 10:00:00.000] [INFO] old line {i}");
            for (int i = 0; i < 500; i++) lines.Add($"[2026-05-22 10:00:00.000] [INFO] recent line {i}");
            File.WriteAllLines(_logPath, lines);

            var result = ParseResult(CallReadLogs(lines: 10, sinceMode: "2026-05-21T00:00:00Z"));
            var matched = result["result"]?["lines"]?.ToString()
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            Assert.NotNull(matched);
            // Only the last 10 are returned, but they must come from the *matching* set.
            Assert.Equal(10, matched!.Length);
            Assert.All(matched, l => Assert.Contains("recent", l));
        }

        [Fact]
        public void A_Filtered_Answer_Keeps_TotalLines_Honest()
        {
            // Issue #342 left `totalLines` reporting the size of the retained window, so
            // it silently changed meaning the moment a filter was added.
            WriteMarkerNearTheStart();

            var result = ParseResult(CallReadLogs(lines: 10, filterCorrelation: "corr-0001"));

            Assert.Equal(200_000, result["result"]!["totalLines"]!.Value<long>());
        }

        [Fact]
        public void Retained_Memory_Does_Not_Grow_With_The_Number_Of_Matches()
        {
            // 200k marker lines and `lines=10`: a filter-then-tail implementation that
            // materialized the matches would retain all 200k.
            var lines = new List<string>();
            for (int i = 0; i < 100_000; i++) lines.Add($"[2026-05-22 10:00:00.000] [INFO] corr-0001 hit {i}");
            File.WriteAllLines(_logPath, lines);

            var result = ParseResult(CallReadLogs(lines: 10, filterCorrelation: "corr-0001"));

            Assert.Equal(10, result["result"]!["retainedLines"]!.Value<long>());
            Assert.Equal(10, result["result"]!["matched"]!.Value<int>());
        }

        [Fact]
        public void A_Scan_Stopped_By_Its_Budget_Says_So_Instead_Of_Claiming_No_Match()
        {
            var previous = Environment.GetEnvironmentVariable("GXMCP_LOG_SCAN_MAX_MB");
            try
            {
                // The smallest budget that still lets a block be read at all.
                Environment.SetEnvironmentVariable("GXMCP_LOG_SCAN_MAX_MB", "1");
                WriteMarkerNearTheStart(total: 400_000);

                var result = ParseResult(CallReadLogs(lines: 10, filterCorrelation: "corr-9999-never-there"));

                Assert.Equal(0, result["result"]!["matched"]!.Value<int>());
                Assert.False(result["result"]!["scanComplete"]!.Value<bool>(),
                    "the scan was cut short, so this is not evidence of absence");
                Assert.True(result["result"]!["scannedBytes"]!.Value<long>() > 0);
                Assert.Contains("NOT evidence", result["result"]!["hint"]?.ToString() ?? string.Empty);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_LOG_SCAN_MAX_MB", previous);
            }
        }

        [Fact]
        public void SinceCrash_Still_Finds_A_Marker_Older_Than_The_Old_Window()
        {
            var lines = new List<string>();
            for (int i = 0; i < 5000; i++) lines.Add($"[2026-05-22 10:00:00.000] [INFO] filler {i}");
            lines.Add("[2026-05-22 11:00:00.000] [ERROR] boom in SyntheticOrder");
            lines.Add("[2026-05-22 11:00:01.000] [INFO] stack frame 1");
            File.WriteAllLines(_logPath, lines);

            // lines=10 is far below the 5000-line distance to the marker.
            var result = ParseResult(CallReadLogs(lines: 10, sinceMode: "crash"));
            var body = result["result"]?["lines"]?.ToString() ?? string.Empty;

            Assert.Equal("ok", result["status"]?.ToString());
            Assert.Contains("boom in SyntheticOrder", body);
            Assert.Contains("stack frame 1", body);
            Assert.True(result["result"]!["crashLineIndex"]!.Value<int>() >= 0);
        }

        [Fact]
        public void A_Pathological_Grep_Still_Degrades_RatherThan_Throwing()
        {
            var longLine = new string('a', 20000) + "b";
            WriteLog(new[] { $"[2026-05-22 10:00:00.000] [INFO] {longLine}" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = ParseResult(CallReadLogs(grepPattern: "(a|aa)+$"));
            sw.Stop();

            Assert.Equal("ok", result["status"]?.ToString());
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"grep filter took {sw.Elapsed}");
        }
    }
}
