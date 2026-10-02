namespace GxMcp.Worker
{
    /// <summary>
    /// Masks credential-shaped values in anything about to be written to a log.
    ///
    /// This existed five times across the two assemblies:
    /// <c>KbImportHelper.LogValue</c>, <c>MacroSuggestionService.LogValue</c> and
    /// <c>Program.Http.LogValue</c> in the Gateway, and here
    /// <c>PreviewService.LogValue</c> plus
    /// <c>SharedWorkerHost.RedactDiagnostic</c>. Four were identical. The fifth
    /// was not, and that is the argument for this class: it had already added
    /// <c>connectionstring</c> to the key list, so a connection string written
    /// through one of the other four was masked only in part. A redaction rule
    /// that lives in five places is only as good as the copy whose author
    /// remembered to update it.
    ///
    /// The Gateway carries the same helper under the same name. The two
    /// assemblies cannot share a type, and a test compares the two pattern
    /// literals so the copies cannot drift apart again.
    /// </summary>
    internal static class LogRedaction
    {
        /// <summary>
        /// The keys whose value is replaced. <c>connectionstring</c> is here
        /// because a connection string carries a password even when it does not
        /// spell the key as one - <c>connectionstring=Server=db;Pwd=secret</c>
        /// has no key this pattern matches on its own, so without the
        /// <c>connectionstring</c> alternative the whole string is logged intact.
        ///
        /// The long keys carry a narrow prefix allowance of word characters plus
        /// <c>.</c>, <c>[</c>, <c>]</c> and <c>-</c>, so a compound key whose
        /// prefix is a letter matches: <c>KBDbPassword=</c>, <c>DbPassword=</c>
        /// and <c>myToken=</c> all leak under a bare word-boundary key. The class
        /// is deliberately narrow so the prefix cannot run across arbitrary text
        /// into a key word.
        ///
        /// <c>pass</c> deliberately does NOT take that prefix, and must not: it is
        /// a separate strict-boundary alternative. Folded into the prefixed group
        /// it would match <c>bypass=1</c> as <c>by</c> + <c>pass</c> and redact an
        /// ordinary diagnostic value, corrupting the message it was meant to
        /// protect. <c>compassion=</c> and <c>aPass=</c> fail the same way. That is
        /// the argument against a future edit "simplifying" the two groups into
        /// one, and the reason the near-miss regression test exists.
        /// </summary>
        internal const string Pattern =
            @"(?is)(?<key>(?:[A-Za-z0-9_.\[\]-]*(?:password|passwd|pwd|token|secret|api[-_]?key|authorization|credential|connectionstring)\b|\bpass\b))\s*[""']?\s*(?<separator>\s*[:=]\s*)(?:"".*?""|'.*?'|(?:Bearer\s+)?[^\s,;}&\]]+)";

        /// <summary>
        /// Replaces the value that follows any <see cref="Pattern"/> key with
        /// <c>&lt;redacted&gt;</c>, keeping the key and its separator so the shape
        /// of the message stays readable.
        ///
        /// Returns the input unchanged when it is null, and never throws: this runs
        /// on the logging path, where an exception here would replace the log line
        /// with the failure to log it.
        /// </summary>
        internal static string Redact(string value)
        {
            try
            {
                return System.Text.RegularExpressions.Regex.Replace(
                    value ?? string.Empty,
                    Pattern,
                    match => match.Groups["key"].Value + match.Groups["separator"].Value + "<redacted>");
            }
            catch
            {
                return value ?? string.Empty;
            }
        }
    }
}