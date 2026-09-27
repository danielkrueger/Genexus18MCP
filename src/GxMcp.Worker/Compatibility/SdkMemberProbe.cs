using System;
using System.Reflection;

namespace GxMcp.Worker.Compatibility
{
    /// <summary>
    /// Binding flags for probing optional Artech SDK members.
    /// </summary>
    /// <remarks>
    /// The SDK exposes helper members whose declaring type changes between GeneXus
    /// majors: a static helper that lives on a base class in one major is declared
    /// directly on the helper in another. A probe that omits
    /// <see cref="BindingFlags.FlattenHierarchy"/> therefore misses the base-class
    /// form and silently reports the capability as unavailable.
    ///
    /// The asymmetry that motivates these flags, measured on .NET Framework 4.8:
    /// an <em>instance</em> member declared on a base class is already found by
    /// <see cref="Type.GetMethod(string, BindingFlags)"/> without
    /// <c>FlattenHierarchy</c>, because instance lookup walks the hierarchy by
    /// default. A <em>static</em> member declared on a base class is <em>not</em>:
    /// it is found only when <c>FlattenHierarchy</c> is supplied. So the flag is
    /// load-bearing for static probes and inert for instance probes, and adding it
    /// uniformly is the only way to stay correct as the SDK reshuffles its
    /// hierarchy between majors.
    ///
    /// <see cref="BindingFlags.DeclaredOnly"/> is deliberately excluded: it
    /// restricts a lookup to the type itself, which is the opposite of what a
    /// compatibility probe wants. Probes that genuinely enumerate one type's own
    /// members keep passing it explicitly.
    /// </remarks>
    internal static class SdkMemberProbe
    {
        /// <summary>Public instance members, including inherited ones.</summary>
        internal const BindingFlags Instance =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        /// <summary>Public static members, including ones inherited from a base type.</summary>
        internal const BindingFlags Static =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        /// <summary>Public static or instance members, including inherited ones.</summary>
        internal const BindingFlags StaticOrInstance =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        /// <summary>
        /// Non-public members are included for explicitly implemented interface
        /// members, which C# emits as private final methods and which
        /// <see cref="BindingFlags.Public"/> alone cannot see.
        /// </summary>
        internal const BindingFlags StaticOrInstanceAnyVisibility =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.FlattenHierarchy;

        /// <summary>
        /// Resolves a single SDK member, returning null when it is absent. Centralized
        /// so a probe cannot accidentally drop the hierarchy flag.
        /// </summary>
        internal static MethodInfo Resolve(Type type, string name, BindingFlags flags, Type[] argumentTypes)
        {
            if (type == null || string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                return type.GetMethod(
                    name,
                    flags,
                    binder: null,
                    types: argumentTypes,
                    modifiers: null);
            }
            catch (AmbiguousMatchException)
            {
                // A member declared on both the type and a base class with an
                // identical signature is genuinely ambiguous; the probe reports
                // "absent" so the caller degrades instead of binding arbitrarily.
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves a single no-argument SDK member using the hierarchy-safe
        /// instance flags.
        /// </summary>
        internal static MethodInfo ResolveNoArgs(Type type, string name)
            => Resolve(type, name, Instance, Type.EmptyTypes);
    }
}
