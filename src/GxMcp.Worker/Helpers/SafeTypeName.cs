using System;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Names a CLR type for a diagnostic message without ever throwing.
    ///
    /// It existed twice, byte-identical, as a private <c>SafeTypeName</c> on
    /// <c>SdkProbeService</c> and on <c>SdkSurfaceProbe</c>. Both use it to describe
    /// what an SDK member looked like when it could not be reached, so the two
    /// probe reports name the same type the same way; a divergence would make two
    /// reports of one object disagree about the type it was looking at.
    ///
    /// <para>"Safe" is the contract: this runs inside the failure path, often while
    /// reporting that reflection over the SDK already failed, so it must not fail
    /// the same way. A null type, a generic type definition that cannot be
    /// resolved, or an argument list that throws all render as <c>?</c> rather than
    /// propagating.</para>
    /// </summary>
    internal static class SafeTypeName
    {
        /// <summary>
        /// The simple name of <paramref name="type"/>, or <c>Definition&lt;Arg,...&gt;</c>
        /// for a constructed generic. Returns <c>?</c> for null and for anything
        /// reflection cannot describe.
        /// </summary>
        internal static string Describe(Type type)
        {
            if (type == null) return "?";
            try
            {
                if (type.IsGenericType)
                {
                    string definition = type.GetGenericTypeDefinition().Name;
                    string args = string.Join(",", type.GetGenericArguments().Select(Describe));
                    return definition + "<" + args + ">";
                }
                return type.Name;
            }
            catch { return "?"; }
        }
    }
}