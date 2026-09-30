using System;
using System.Reflection;
using System.Xml;

namespace GxMcp.Worker.Compatibility
{
    /// <summary>
    /// Creates web-form controls through the SDK member the running GeneXus major
    /// actually exposes. GeneXus 18 exposes static
    /// <c>WebTagFactory.Create(XmlNode, KBObject, IWebTag, bool)</c>; older or
    /// newer majors may rename, reshape, or drop it. The probe is structural —
    /// name plus an argument-compatible 4-parameter shape with a trailing bool —
    /// so fake shapes in tests exercise the same path as the real SDK type.
    /// Follows the <see cref="SdkDeletionAdapter"/> contract: absent member
    /// reports false, an SDK rejection propagates instead of degrading into a
    /// false "unsupported".
    /// </summary>
    internal static class WebTagFactoryAdapter
    {
        internal const string FactoryTypeName = "Artech.Genexus.Common.Parts.WebForm.WebTagFactory";

        /// <summary>
        /// Resolves the factory type from the assemblies loaded in the worker process,
        /// falling back to explicitly loading the assembly that carries it. Returns null
        /// when the shape is unavailable — the caller keeps the raw path. Takes no
        /// shortcut through a fixed assembly reference so the probe stays valid across
        /// GeneXus majors.
        /// </summary>
        internal static Type TryResolveFactoryType()
        {
            Type found = GxMcp.Worker.Helpers.WebFormSdkReflection.FindType(FactoryTypeName);
            if (found != null) return found;
            try
            {
                return Assembly.Load("Artech.Genexus.Common").GetType(FactoryTypeName, throwOnError: false);
            }
            catch { return null; }
        }

        /// <summary>
        /// Binds the factory's <c>Create</c> to a node. The trailing <c>bool</c> argument
        /// is the SDK's read-only flag; a new control is never read-only, so it is
        /// passed as false here rather than exposed as a parameter no caller can vary.
        ///
        /// Returns false when no <c>Create</c> matches, when two match equally well
        /// (binding one arbitrarily would be a guess), or when an instance overload is
        /// selected but the factory cannot be constructed. An SDK rejection propagates.
        /// </summary>
        internal static bool TryCreateTagOn(
            Type factoryType,
            XmlNode node,
            object kbObj,
            object parentTag,
            out object tag)
        {
            tag = null;
            if (factoryType == null) return false;
            if (node == null) return false;

            object[] args = { node, kbObj, parentTag, false };
            MethodInfo best = SelectCreateOverload(factoryType, args);
            if (best == null) return false;

            object target = null;
            if (!best.IsStatic)
            {
                target = TryInstantiate(factoryType);
                if (target == null) return false;
            }

            try
            {
                tag = best.Invoke(target, args);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException(ex.InnerException?.Message ?? ex.Message, ex);
            }
        }

        /// <summary>
        /// The most argument-compatible <c>Create(XmlNode, KBObject, IWebTag, bool)</c>,
        /// or null when none matches or two match equally well (fail closed, same rule
        /// the member probe applies to <c>AmbiguousMatchException</c>).
        /// </summary>
        private static MethodInfo SelectCreateOverload(Type factoryType, object[] args)
        {
            MethodInfo best = null;
            int bestScore = -1;
            bool ambiguous = false;

            foreach (MethodInfo candidate in factoryType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                if (candidate.IsSpecialName) continue;
                if (!string.Equals(candidate.Name, "Create", StringComparison.Ordinal)) continue;
                if (!candidate.IsStatic && !HasDefaultConstructor(factoryType)) continue;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != 4) continue;
                if (parameters[3].ParameterType != typeof(bool)) continue;

                int score = CompatibilityScore(parameters, args);
                if (score < 0) continue;

                if (best != null && score == bestScore) { ambiguous = true; continue; }
                if (score > bestScore) { bestScore = score; ambiguous = false; best = candidate; }
            }

            return ambiguous ? null : best;
        }

        /// <summary>
        /// How well the supplied arguments fit the overload's parameters, or -1 when any
        /// argument is incompatible. A null argument fits any reference-type parameter and
        /// disqualifies a value-type one, so an unresolvable parent tag narrows the
        /// candidates rather than failing them all.
        /// </summary>
        private static int CompatibilityScore(ParameterInfo[] parameters, object[] args)
        {
            int score = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                object arg = args[i];
                if (arg == null)
                {
                    if (parameters[i].ParameterType.IsValueType) return -1;
                    continue;
                }
                if (!parameters[i].ParameterType.IsInstanceOfType(arg)) return -1;
                score++;
            }
            return score;
        }

        private static bool HasDefaultConstructor(Type factoryType)
        {
            try { return factoryType.GetConstructor(Type.EmptyTypes) != null; }
            catch { return false; }
        }

        private static object TryInstantiate(Type factoryType)
        {
            try { return factoryType.GetConstructor(Type.EmptyTypes)?.Invoke(null); }
            catch { return null; }
        }
    }
}
