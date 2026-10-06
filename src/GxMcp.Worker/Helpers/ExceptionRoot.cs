using System;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Reflection;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// SDK calls made by reflection surface their real failure inside a
    /// <see cref="TargetInvocationException"/> whose own message is the generic
    /// "Exception has been thrown by the target of an invocation" (#419).
    /// </summary>
    internal static class ExceptionRoot
    {
        private const int MaxFrames = 5;

        /// <summary>Unwraps reflection and single-cause aggregate wrappers to the original exception.</summary>
        internal static Exception Unwrap(Exception ex)
        {
            while (ex != null)
            {
                Exception inner = null;
                if (ex is TargetInvocationException tie) inner = tie.InnerException;
                else if (ex is AggregateException agg) inner = agg.Flatten().InnerExceptions.Count == 1 ? agg.Flatten().InnerExceptions[0] : null;
                if (inner == null) return ex;
                ex = inner;
            }
            return null;
        }

        /// <summary>The root cause's message, for use in place of the wrapper's.</summary>
        internal static string Message(Exception ex) => Unwrap(ex)?.Message;

        /// <summary>Method names of the first stack frames of the root cause (no paths or arguments).</summary>
        internal static string FailureTrace(Exception ex)
        {
            var root = Unwrap(ex);
            if (root == null) return null;
            var methods = (new StackTrace(root, false).GetFrames() ?? new StackFrame[0])
                .Select(frame => frame.GetMethod())
                .Where(method => method != null)
                .Select(method => method.DeclaringType == null ? method.Name : method.DeclaringType.FullName + "." + method.Name)
                .Take(MaxFrames).ToArray();
            if (methods.Length > 0) return string.Join(" <- ", methods);

            // Serialized/remote exceptions can retain only text, with no runtime frames.
            // Accept only qualified method signatures, never paths or arbitrary messages;
            // the single-word frame prefix is intentionally not tied to a UI language.
            if (root.StackTrace == null) return null;
            var frames = root.StackTrace.Split('\n')
                .Select(line => Regex.Match(line, @"^\s*\S+\s+([\w+`<>]+(?:\.[\w+`<>]+)+)\s*\("))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value)
                .Take(MaxFrames);
            return string.Join(" <- ", frames);
        }

        /// <summary>Logs one line carrying the root exception type, message and trace.</summary>
        internal static void Log(string context, Exception ex)
        {
            var root = Unwrap(ex);
            Logger.Error(context + ": " + root?.GetType().Name + ": " + root?.Message + " [" + FailureTrace(ex) + "]");
        }
    }
}
