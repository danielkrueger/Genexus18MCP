using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The next step offered when a call could not proceed because no KB was open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every such refusal points at the same thing - <c>genexus_kb</c> with
    /// <c>action=open</c> - and six tools were each writing that step out by hand. The
    /// tool name and the action are not the caller's to choose: a next step naming a
    /// tool which does not exist, or spelling the action differently, strands exactly
    /// the caller who is already lost. So those two facts live here.
    /// </para>
    ///
    /// <para>
    /// The <c>why</c> and the extra arguments stay per-tool, deliberately. Telling an
    /// agent "open the target KB before deleting" is what makes the step useful rather
    /// than a generic instruction it would apply before a delete and get wrong.
    /// </para>
    ///
    /// <para>
    /// Only the <em>next step</em> is shared. The refusals themselves differ in their
    /// error code - <c>NoKbOpen</c> in four places and <c>KbNotOpen</c> in four others
    /// - and in their wording. That is not corrected here: a client matches on the
    /// code, so collapsing the two would change what existing callers see.
    /// </para>
    /// </remarks>
    internal static class KbOpenNextStep
    {
        /// <summary>
        /// A step pointing the caller at <c>genexus_kb action=open</c>.
        /// </summary>
        /// <param name="why">
        /// Why this tool needs a KB open - what the caller was trying to do.
        /// </param>
        /// <param name="extraArgs">
        /// Additional arguments merged into the step's <c>args</c>, for a tool that
        /// wants to show the shape of the call it could have made.
        /// </param>
        internal static JObject Step(string why, JObject extraArgs = null)
        {
            var args = new JObject { ["action"] = "open" };
            if (extraArgs != null)
            {
                foreach (var property in extraArgs.Properties())
                {
                    // The action wins: a step asking for anything but opening a KB
                    // would defeat the point of sharing it.
                    if (args[property.Name] == null) args[property.Name] = property.Value;
                }
            }
            return McpResponse.NextStep("genexus_kb", args, why);
        }
    }
}