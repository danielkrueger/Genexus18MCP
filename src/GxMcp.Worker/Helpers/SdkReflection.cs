using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Canonical reflection and text helpers for talking to the GeneXus SDK.
    ///
    /// <para>These members existed as ~90 near-identical private copies scattered
    /// across services and helpers. The copies were not interchangeable: they differed
    /// in visibility flags, in whether they guarded indexer properties, in whether they
    /// checked <c>CanWrite</c> before <c>SetValue</c>, and in what they returned on
    /// failure. A control could therefore be found on one path and missed on another
    /// for reasons no caller could see. One implementation, one set of semantics.</para>
    ///
    /// <para>Conventions, so a future copy knows what to write:
    /// <list type="bullet">
    /// <item>Every member never throws. SDK reflection is a probe, and a probe that
    /// throws is a probe that takes down the caller.</item>
    /// <item>Every member reads and writes across the whole base-type chain with
    /// public and non-public visibility. SDK types shadow members in base classes.</item>
    /// <item>Every member rejects indexer properties — <c>GetValue</c> on one throws
    /// for a missing index.</item>
    /// <item>A <c>Try</c> prefix means exactly that; see
    /// <see cref="TryInvokeNoArgs"/> for the case that got this wrong.</item>
    /// </list></para>
    /// </summary>
    internal static class SdkReflection
    {
        private const BindingFlags AnyInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ---------------------------------------------------------------- members

        /// <summary>
        /// Locates an SDK type by full name across the loaded assemblies. Reflection by
        /// name rather than a compile-time reference is what keeps the Worker buildable
        /// against more than one GeneXus major. Null when the shape is not loaded.
        /// </summary>
        internal static Type FindType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// A method anywhere in the type's inheritance chain, or null. Searching the
        /// chain matters because SDK members are frequently declared on a base class
        /// and hidden by a derived declaration.
        /// </summary>
        internal static MethodInfo FindMethod(Type type, string name, params Type[] parameterTypes)
        {
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                var method = current.GetMethod(name, AnyInstance, binder: null,
                    types: parameterTypes ?? new Type[0], modifiers: null);
                if (method != null) return method;
            }
            return null;
        }

        /// <summary>A field anywhere in the type's inheritance chain, or null.</summary>
        internal static FieldInfo FindField(Type type, string name)
        {
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                var field = current.GetField(name, AnyInstance);
                if (field != null) return field;
            }
            return null;
        }

        /// <summary>A non-indexer property by name, or null. Case-insensitive.</summary>
        internal static PropertyInfo FindProperty(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            var property = type.GetProperty(name, AnyInstance | BindingFlags.IgnoreCase);
            if (IsReadable(property)) return property;
            // A derived type can declare a member the base also declares; the first
            // lookup then throws AmbiguousMatchException, so walk declared-only.
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                var declared = current.GetProperty(name,
                    BindingFlags.DeclaredOnly | AnyInstance | BindingFlags.IgnoreCase);
                if (IsReadable(declared)) return declared;
            }
            return null;
        }

        private static bool IsReadable(PropertyInfo property) =>
            property != null && property.CanRead && property.GetIndexParameters().Length == 0;

        // ------------------------------------------------------------------ reads

        /// <summary>Reads a non-indexer property, or null when absent or unreadable.</summary>
        internal static object Read(object target, string name)
        {
            if (target == null) return null;
            try { return FindProperty(target.GetType(), name)?.GetValue(target, null); }
            catch { return null; }
        }

        /// <summary>Reads a property as a string, or null.</summary>
        internal static string ReadString(object target, string name)
        {
            var value = Read(target, name);
            if (value == null) return null;
            try { return value as string ?? value.ToString(); }
            catch { return null; }
        }

        /// <summary>
        /// Reads the first of <paramref name="names"/> that resolves. GeneXus has used
        /// several names for the same concept across majors (<c>Dirty</c>/<c>IsDirty</c>,
        /// <c>KBObject</c>/<c>ContainerObject</c>), and the alias list is the whole point.
        /// </summary>
        internal static object ReadFirst(object target, params string[] names)
        {
            foreach (var name in names ?? new string[0])
            {
                var value = Read(target, name);
                if (value != null) return value;
            }
            return null;
        }

        /// <summary>
        /// Invokes a no-argument instance method anywhere in the chain.
        ///
        /// <para>Never throws, including when the method itself throws: a <c>Try</c>
        /// helper that propagates is worse than no helper, because the caller stops
        /// guarding the call. This is the bug that motivated the class.</para>
        /// </summary>
        internal static object TryInvokeNoArgs(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name)) return null;
            try { return FindMethod(target.GetType(), name)?.Invoke(target, null); }
            catch { return null; }
        }

        /// <summary>Invokes a method and coerces a boolean result. False when absent or throwing.</summary>
        internal static bool TryInvokeBool(object target, string name)
        {
            return TryInvokeNoArgs(target, name) is bool flag && flag;
        }

        /// <summary>Invokes a method and coerces a string result. Null when absent or throwing.</summary>
        internal static string TryInvokeString(object target, string name)
        {
            var value = TryInvokeNoArgs(target, name);
            if (value == null) return null;
            try { return value as string ?? value.ToString(); }
            catch { return null; }
        }

        // ----------------------------------------------------------------- writes

        /// <summary>
        /// Sets a writable, non-indexer property. Returns false when the property is
        /// absent, read-only, an indexer, or the type does not match — never throws.
        /// The <c>CanWrite</c> and type checks are load-bearing: calling
        /// <c>SetValue</c> without them throws on a read-only SDK member, and the
        /// caller's outer catch then leaves the object silently unchanged.
        /// </summary>
        internal static bool TrySet(object target, string name, object value)
        {
            if (target == null || string.IsNullOrEmpty(name)) return false;
            try
            {
                var property = FindProperty(target.GetType(), name);
                if (property == null || !property.CanWrite || property.GetIndexParameters().Length != 0) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Sets a boolean property. False when absent, read-only, or not a bool.</summary>
        internal static bool TrySetBool(object target, string name, bool value)
        {
            var property = target == null ? null : FindProperty(target.GetType(), name);
            if (property == null || property.PropertyType != typeof(bool)) return false;
            return TrySet(target, name, value);
        }

        /// <summary>
        /// Marks an SDK object dirty using whichever alias the major exposes, then
        /// reports whether either was set. The save pipeline calls this on both the
        /// part and its owning object, and a false here is the difference between a
        /// persisted change and a silently dropped one.
        /// </summary>
        internal static bool MarkDirty(object target)
        {
            if (target == null) return false;
            return TrySetBool(target, "Dirty", true) || TrySetBool(target, "IsDirty", true);
        }

        // ------------------------------------------------------------- identity

        /// <summary>
        /// An object's <c>EntityKey</c>, <c>EntityTypeGuid</c> and <c>EntityId</c>,
        /// read once. These three travel together and every caller re-derived them
        /// with the same three-property chain.
        /// </summary>
        internal static bool TryGetEntityIdentity(object obj, out string entityKey, out string entityTypeGuid, out int? entityId)
        {
            entityKey = null;
            entityTypeGuid = null;
            entityId = null;
            if (obj == null) return false;
            try
            {
                entityKey = ReadString(obj, "EntityKey");
                entityTypeGuid = ReadString(obj, "EntityTypeGuid");
                var id = Read(obj, "EntityId");
                if (id is int intId) entityId = intId;
                else if (id != null) { int parsed; if (int.TryParse(id.ToString(), out parsed)) entityId = parsed; }
                return !string.IsNullOrEmpty(entityKey);
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ text

        /// <summary>
        /// Truncates to at most <paramref name="max"/> characters, ellipsis included.
        ///
        /// <para>The result length is bounded by <paramref name="max"/>, not
        /// <c>max + 1</c>. The copies this replaced disagreed: most appended the
        /// ellipsis after slicing, producing one character more than the caller
        /// asked for, while two sliced to <c>max - 1</c> and matched. Any caller
        /// comparing lengths, or any budget computed from the returned width, got a
        /// different answer depending on which service produced the string.</para>
        /// </summary>
        internal static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || max <= 0) return string.Empty;
            if (value.Length <= max) return value;
            return value.Substring(0, max - 1) + "…";
        }

        /// <summary>
        /// Lowercase hex SHA-256 of a string, or null when the value is null.
        ///
        /// <para>Null propagates rather than hashing as empty. The three copies this
        /// replaced disagreed: two hashed null as the empty string, one returned null.
        /// That difference is a false-positive verification. A restore check of the
        /// shape <c>Equals(Sha256(restored), Sha256(before))</c> with both sides null
        /// evaluates true, so a snapshot restore that wrote nothing at all reported
        /// "restored". Callers must therefore treat a null hash as "nothing to
        /// compare", never as a match.</para>
        /// </summary>
        internal static string Sha256Hex(string value)
        {
            if (value == null) return null;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        /// <summary>SHA-256 with null folded into the empty string, for the cases that do mean the same.</summary>
        internal static string Sha256OrEmpty(string value) => Sha256Hex(value ?? string.Empty);

        // ------------------------------------------------------- property bag

        /// <summary>
        /// Reads through the SDK's own <c>GetPropertyValue</c> property bag, which is
        /// how descriptor values are exposed when the CLR property does not exist.
        /// </summary>
        internal static object TryGetPropertyBagValue(object target, string name)
            => ReflectionHelper.TryGetPropertyBagValue(target, name);

        /// <summary>Writes through the SDK's own <c>SetPropertyValue</c> property bag.</summary>
        internal static bool TrySetPropertyBagValue(object target, string name, object value)
            => ReflectionHelper.TrySetPropertyBagValue(target, name, value);

        // ---------------------------------------------------------- safe probes

        /// <summary>
        /// Runs a probe and swallows any failure. For SDK getters that have no
        /// meaningful "absent" signal of their own. Prefer a typed member above when
        /// one exists — this hides the failure rather than describing it.
        /// </summary>
        internal static T Safe<T>(Func<T> probe, T fallback = default(T))
        {
            try { return probe(); }
            catch { return fallback; }
        }
    }
}
