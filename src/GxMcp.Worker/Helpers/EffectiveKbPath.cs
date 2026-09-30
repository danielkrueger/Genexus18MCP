using System;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Which KB a call is about, when the caller may have named one explicitly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An explicitly supplied path wins and is used verbatim: a caller naming a KB is
    /// asking about that KB, not about whichever one the session has open. Otherwise
    /// the answer is whichever KB is open, and no KB at all is <c>null</c> rather than
    /// an error - callers turn that into their own <c>NoKbOpen</c> refusal with the
    /// wording their tool needs.
    /// </para>
    ///
    /// <para>
    /// It never throws. Callers use the result to decide whether to refuse, so a
    /// resolution failure arriving as an exception would replace a clear
    /// <c>NoKbOpen</c> with a stack trace in a place that has no handler for one. That
    /// is why the lookup is wrapped even though <see cref="Services.KbService.GetKbPath"/>
    /// already catches internally and cannot throw today: this is a promise to its
    /// callers, and that method is free to stop honouring it.
    /// </para>
    ///
    /// <para>
    /// A <c>null</c> service is tolerated rather than treated as an error, because
    /// these callers are constructed without one in tests and resolution has to stay
    /// usable there - which is also why this is a static taking the service rather
    /// than an instance method that would need dereferencing first.
    /// </para>
    /// </remarks>
    internal static class EffectiveKbPath
    {
        /// <summary>
        /// The KB path this call is about, or <c>null</c> when none was named and none
        /// is open. Never throws.
        /// </summary>
        internal static string Resolve(Services.KbService kbService, string kbPathOverride)
        {
            if (!string.IsNullOrEmpty(kbPathOverride)) return kbPathOverride;
            try { return kbService?.GetKbPath(); }
            catch { return null; }
        }
    }
}