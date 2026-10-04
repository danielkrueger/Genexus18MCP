using System;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Decides when the lite-index walk writes a resume checkpoint.
    ///
    /// <para>
    /// Issue #373. The walk wrote the whole accumulated snapshot every
    /// <c>checkpointInterval</c> objects, so a walk of N objects wrote about
    /// <c>sum(k*C*s) = O(N^2 / C)</c> bytes - on a large KB, many times the final index
    /// size, all on the indexing path. Doubling the interval after each write bounds the
    /// total at O(N); the price is more rework after an interruption, which the resume
    /// semantics already tolerate because a checkpoint is re-walkable work, not a
    /// correctness boundary.
    /// </para>
    ///
    /// <para>
    /// Kept as its own type so the growth property is asserted directly rather than
    /// inferred from a walk that needs a live SDK enumerator.
    /// </para>
    /// </summary>
    internal sealed class LiteCheckpointSchedule
    {
        /// <summary>Objects between the first and second checkpoint.</summary>
        internal const int FirstInterval = 2000;

        /// <summary>Ceiling on the interval, so it cannot overflow a long walk.</summary>
        internal const int MaxInterval = 4 * 1024 * 1024;

        private long _nextAt;
        private int _interval;

        internal LiteCheckpointSchedule(long alreadyProcessed = 0)
        {
            _interval = FirstInterval;
            // Resuming deep into a walk continues the doubling rather than restarting it,
            // so a resume at 20k does not immediately re-checkpoint the same state.
            long start = alreadyProcessed * 2L;
            _nextAt = start > FirstInterval ? start : FirstInterval;
        }

        /// <summary>Objects currently scheduled between checkpoints.</summary>
        internal int Interval => _interval;

        /// <summary>Object count at which the next checkpoint is due.</summary>
        internal long NextAt => _nextAt;

        /// <summary>Checkpoints written so far.</summary>
        internal int Writes { get; private set; }

        /// <summary>
        /// Whether a checkpoint is due at <paramref name="cumulativeProcessed"/>, which is
        /// the resume point plus what this walk has walked so far. Call
        /// <see cref="Record"/> only when this returns true.
        /// </summary>
        internal bool IsDue(long cumulativeProcessed) => cumulativeProcessed >= _nextAt;

        /// <summary>Advances to the next checkpoint, doubling the interval.</summary>
        internal void Record()
        {
            Writes++;
            _nextAt += _interval;
            long doubled = _interval * 2L;
            _interval = doubled > MaxInterval ? MaxInterval : (int)doubled;
        }
    }
}