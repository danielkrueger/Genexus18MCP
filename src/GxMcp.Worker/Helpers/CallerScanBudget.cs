using System;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The work budget for one detailed caller-site scan.
    ///
    /// <para>
    /// Issue #343. <c>FindCallerSites</c> walked every recorded caller, read
    /// Source/Events/Rules in full and parsed all of it, with no page boundary, no work
    /// budget and no cancellation point. A high-fan-in target - a shared helper called by
    /// thousands of objects - could monopolise the SDK lane past a client deadline and
    /// leave nothing resumable behind: the work was either done or abandoned.
    /// </para>
    ///
    /// <para>
    /// This bounds four independent dimensions, because any one of them alone is
    /// insufficient. Counting callers ignores source size; counting bytes ignores elapsed
    /// time on a slow disk; counting results ignores a target with many callers that
    /// simply do not call it. Elapsed time is included because the caller-visible failure
    /// is a deadline, not an internal counter.
    /// </para>
    ///
    /// <para>
    /// Every budget is charged before the work it pays for, so the scan stops *between*
    /// units of work rather than after exceeding one. That is what makes the stop point
    /// resumable: the caller knows exactly which caller and part it stopped at.
    /// </para>
    ///
    /// <para>
    /// Pure and clock-injected, so the stopping rules are testable without an SDK, a
    /// filesystem or a real deadline.
    /// </para>
    /// </summary>
    internal sealed class CallerScanBudget
    {
        internal const int DefaultMaxCallers = 50;
        internal const long DefaultMaxSourceBytes = 4L * 1024 * 1024;
        internal const int DefaultMaxResults = 500;
        internal static readonly TimeSpan DefaultMaxElapsed = TimeSpan.FromSeconds(10);

        private readonly long _maxSourceBytes;
        private readonly int _maxResults;
        private readonly TimeSpan _maxElapsed;
        private readonly Func<DateTime> _clock;

        private int _maxCallers;
        private DateTime _startedAt;
        private bool _started;

        /// <summary>Callers already scanned. Also the resume offset within the caller list.</summary>
        public int CallersScanned { get; private set; }

        /// <summary>Source bytes read so far.</summary>
        public long SourceBytesRead { get; private set; }

        /// <summary>Call sites recorded so far.</summary>
        public int Results { get; private set; }

        /// <summary>Why the scan stopped, or null while it may continue.</summary>
        public string StopReason { get; private set; }

        /// <summary>True when no budget was hit, so the answer covers every caller.</summary>
        public bool IsComplete => StopReason == null;

        public CallerScanBudget(
            int maxCallers = DefaultMaxCallers,
            long maxSourceBytes = DefaultMaxSourceBytes,
            int maxResults = DefaultMaxResults,
            TimeSpan? maxElapsed = null,
            Func<DateTime> clock = null)
        {
            // A caller-supplied budget of zero would stop before any work and report a
            // truncated answer with nothing to resume from. Treat non-positive as the
            // default instead: "unbounded-ish" is a better failure than "always empty".
            _maxCallers = maxCallers > 0 ? maxCallers : DefaultMaxCallers;
            _maxSourceBytes = maxSourceBytes > 0 ? maxSourceBytes : DefaultMaxSourceBytes;
            _maxResults = maxResults > 0 ? maxResults : DefaultMaxResults;
            _maxElapsed = maxElapsed.HasValue && maxElapsed.Value > TimeSpan.Zero
                ? maxElapsed.Value
                : DefaultMaxElapsed;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public int MaxCallers => _maxCallers;
        public long MaxSourceBytes => _maxSourceBytes;
        public int MaxResults => _maxResults;
        public TimeSpan MaxElapsed => _maxElapsed;

        /// <summary>
        /// Begins the elapsed measurement. Separated from the constructor so a budget can
        /// be built (and its limits asserted) without starting a scan.
        /// </summary>
        public void Start() { _startedAt = _clock(); _started = true; }

        /// <summary>
        /// Charges one caller's work before any of it happens. Returns false once the
        /// caller budget or the elapsed budget is spent.
        /// </summary>
        public bool TryBeginCaller()
        {
            if (!CanContinue) return false;
            if (CallersScanned >= _maxCallers) { Stop("max_callers"); return false; }
            if (ElapsedExhausted) { Stop("max_elapsed_ms"); return false; }
            return true;
        }

        /// <summary>
        /// Charges the bytes of one source. A source's size is only knowable after reading
        /// it, so this cannot be charged up front; what it does guarantee is that a source
        /// that does not fit contributes nothing. The caller must discard that source's
        /// results and stop, which is what keeps the answer bounded.
        ///
        /// <para>
        /// Once stopped, every charge returns false and changes nothing. Without that, a
        /// caller that kept charging past the stop would keep growing the counters it is
        /// supposed to be bounding, and the reported totals would not describe the work
        /// that was actually kept.
        /// </para>
        /// </summary>
        public bool TryChargeBytes(long bytes)
        {
            if (!CanContinue) return false;
            if (bytes < 0) bytes = 0;
            if (SourceBytesRead + bytes > _maxSourceBytes) { Stop("max_source_bytes"); return false; }
            SourceBytesRead += bytes;
            return true;
        }

        /// <summary>Charges one recorded call site.</summary>
        public bool TryChargeResult()
        {
            if (!CanContinue) return false;
            if (Results >= _maxResults) { Stop("max_results"); return false; }
            Results++;
            return true;
        }

        /// <summary>
        /// Records that one caller (and all of its parts) was fully processed, advancing
        /// the resume offset. Called only after the caller's parts are done, so a resume
        /// never re-reads a partially scanned caller and never skips one.
        /// </summary>
        public void CompleteCaller() => CallersScanned++;

        /// <summary>
        /// Forces the scan to stop at the next boundary. Used by cancellation, so an
        /// abandoned scan reports an honest partial result rather than a complete one.
        /// </summary>
        public void Cancel() => Stop("cancelled");

        private bool CanContinue => StopReason == null;

        private bool ElapsedExhausted
        {
            get
            {
                if (!_started) return false;
                return (_clock() - _startedAt) >= _maxElapsed;
            }
        }

        /// <summary>Milliseconds spent so far, for the response's honesty metadata.</summary>
        public long ElapsedMs
        {
            get
            {
                if (!_started) return 0;
                long ms = (long)(_clock() - _startedAt).TotalMilliseconds;
                return ms < 0 ? 0 : ms;
            }
        }

        private void Stop(string reason)
        {
            // First reason wins: it is the one that actually ended the scan, and a later
            // budget noticing it was already spent would misattribute the stop.
            if (StopReason == null) StopReason = reason;
        }
    }
}