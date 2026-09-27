using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Result of an apply/re-apply call — kept minimal because the SDK does not
    /// surface a structured return for first-time apply (void) and only a bool
    /// for re-apply. Both are normalized into this shape.
    /// </summary>
    public class PatternApplyResult
    {
        public IList<string> GeneratedObjects { get; set; } = new List<string>();
        public IList<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Engine boundary so tests can mock the live SDK without a KB or license.
    /// </summary>
    public interface IPatternEngineAdapter
    {
        /// <summary>Returns a PatternDefinition (as object) or null when unavailable.</summary>
        object GetPatternDefinition(Guid patternId);

        /// <summary>Returns the existing PatternInstance for (object, patternId), or null.</summary>
        object GetPatternInstance(KBObject parent, Guid patternId);

        /// <summary>First-time apply. SDK signature is void; throws are propagated.</summary>
        PatternApplyResult ApplyPattern(KBObject parent, object patternDefinition, JObject settings);

        /// <summary>Re-apply against an existing PatternInstance.</summary>
        PatternApplyResult ReapplyPattern(object patternInstance, JObject settings);
    }

    /// <summary>
    /// Live implementation. Loads Artech.Packages.Patterns.dll by reflection so
    /// the worker compiles without a hard reference. All calls return null /
    /// throw a controlled NotAvailable status when the assembly is missing,
    /// which the service surfaces as "pattern_unavailable".
    /// </summary>
    internal class ReflectionPatternEngineAdapter : IPatternEngineAdapter
    {
        private const string PatternsAssemblyName = "Artech.Packages.Patterns";
        private const string PatternEngineTypeName = "Artech.Packages.Patterns.PatternEngine";
        private const string PatternInstanceTypeName = "Artech.Packages.Patterns.Objects.PatternInstance";

        private readonly object _lock = new object();
        private bool _probed;
        private Assembly _patternsAssembly;
        private Type _patternEngineType;
        private Type _patternInstanceType;
        private MethodInfo _getPatternDefinition;
        private MethodInfo _applyPatternFirst;     // ApplyPattern(KBObject, PatternDefinition)
        private MethodInfo _applyPatternReapply;   // ApplyPattern(PatternInstance, ApplySettings)
        private MethodInfo _patternInstanceGet;    // PatternInstance.Get(KBObject, Guid)
        private Type _applySettingsType;           // second parameter of _applyPatternReapply

        private bool EnsureProbed()
        {
            if (_probed) return _patternEngineType != null;
            lock (_lock)
            {
                if (_probed) return _patternEngineType != null;
                try
                {
                    _patternsAssembly = LoadPatternsAssembly();
                    if (_patternsAssembly != null)
                    {
                        _patternEngineType = _patternsAssembly.GetType(PatternEngineTypeName, false);
                        _patternInstanceType = _patternsAssembly.GetType(PatternInstanceTypeName, false);
                    }

                    if (_patternEngineType != null)
                    {
                        _getPatternDefinition = _patternEngineType.GetMethod(
                            "GetPatternDefinition",
                            BindingFlags.Public | BindingFlags.Static,
                            null, new[] { typeof(Guid) }, null);

                        // Disambiguate by parameter count; concrete parameter types are
                        // resolved at call time via assembly-loaded Type references.
                        // Disambiguation bug fix (F16): `IsAssignableFrom(KBObject)` matched
                        // BOTH overloads because PatternInstance INHERITS from KBObject. Result:
                        // both methods bound to _applyPatternFirst, _applyPatternReapply stayed
                        // null, and reapply silently failed. Disambiguate by EXACT type now —
                        // the first overload is (KBObject, PatternDefinition), the reapply is
                        // (PatternInstance, ApplySettings).
                        foreach (var m in _patternEngineType.GetMethods(Compatibility.SdkMemberProbe.Static))
                        {
                            if (m.Name != "ApplyPattern") continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 2) continue;
                            // Reapply: first param is PatternInstance (or a subtype).
                            if (_patternInstanceType != null && _patternInstanceType.IsAssignableFrom(ps[0].ParameterType))
                            {
                                _applyPatternReapply = m;
                                _applySettingsType = ps[1].ParameterType;
                            }
                            // First-apply: first param is KBObject EXACTLY (or generic KBObject
                            // base, not a PatternInstance-derived subtype).
                            else if (ps[0].ParameterType == typeof(KBObject))
                            {
                                _applyPatternFirst = m;
                            }
                        }
                    }

                    if (_patternInstanceType != null)
                    {
                        _patternInstanceGet = _patternInstanceType.GetMethod(
                            "Get",
                            BindingFlags.Public | BindingFlags.Static,
                            null, new[] { typeof(KBObject), typeof(Guid) }, null);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("PatternEngine probe failed: " + ex.Message);
                }
                _probed = true;
                return _patternEngineType != null;
            }
        }

        private static Assembly LoadPatternsAssembly()
        {
            // 1. Already loaded?
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, PatternsAssemblyName, StringComparison.OrdinalIgnoreCase));
            if (loaded != null) return loaded;

            // 2. Try the standard install path under Packages\.
            string gxPath = Environment.GetEnvironmentVariable("GX_PATH");
            if (string.IsNullOrEmpty(gxPath))
                gxPath = @"C:\Program Files (x86)\GeneXus\GeneXus18";

            string candidate = Path.Combine(gxPath, "Packages", PatternsAssemblyName + ".dll");
            if (File.Exists(candidate))
            {
                try { return Assembly.LoadFrom(candidate); }
                catch (Exception ex) { Logger.Warn("LoadFrom " + candidate + " failed: " + ex.Message); }
            }

            // 3. Last-resort: by name (will hit fusion's probing paths).
            try { return Assembly.Load(PatternsAssemblyName); }
            catch { return null; }
        }

        public object GetPatternDefinition(Guid patternId)
        {
            if (!EnsureProbed() || _getPatternDefinition == null) return null;
            try { return _getPatternDefinition.Invoke(null, new object[] { patternId }); }
            catch (TargetInvocationException tie)
            {
                Logger.Warn("GetPatternDefinition threw: " + (tie.InnerException?.Message ?? tie.Message));
                return null;
            }
        }

        public object GetPatternInstance(KBObject parent, Guid patternId)
        {
            if (!EnsureProbed() || _patternInstanceGet == null) return null;
            try { return _patternInstanceGet.Invoke(null, new object[] { parent, patternId }); }
            catch (TargetInvocationException) { return null; }
        }

        public PatternApplyResult ApplyPattern(KBObject parent, object patternDefinition, JObject settings)
        {
            if (!EnsureProbed() || _applyPatternFirst == null)
                throw new InvalidOperationException("PatternEngine.ApplyPattern(KBObject, PatternDefinition) not found");

            // First-apply overload is void ApplyPattern(KBObject, PatternDefinition) — no
            // ApplySettings slot. Settings only flow on re-apply. If the caller supplied
            // settings on first-apply, surface a log line so they know the call defaulted.
            if (settings != null && settings.Count > 0)
            {
                Logger.Info("ApplyPattern (first-apply): settings ignored on this overload — defaults applied. Use reapply=true after apply to project settings.");
            }

            try
            {
                _applyPatternFirst.Invoke(null, new[] { parent, patternDefinition });
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }

            // The SDK doesn't surface generated-object names from the void overload.
            // Best-effort: scan the new PatternInstance for owned objects after apply.
            return new PatternApplyResult();
        }

        // Public surface so PatternApplyService can re-apply on a host that was just
        // attached via PatternInstancePackageInterface (no probe re-run needed).
        public bool HasReapplyOverload()
        {
            EnsureProbed();
            return _applyPatternReapply != null;
        }

        public void InvokeReapply(object patternInstanceObj, JObject settings)
        {
            EnsureProbed();
            if (_applyPatternReapply == null) throw new InvalidOperationException("Reapply overload not bound");
            // ApplySettings is needed by the SDK (passing null causes NRE inside).
            // Materialise an empty instance via parameterless ctor and project caller
            // settings on top if provided.
            object applySettings = null;
            if (_applySettingsType != null)
            {
                try
                {
                    var ctor = _applySettingsType.GetConstructor(Type.EmptyTypes);
                    applySettings = ctor != null
                        ? ctor.Invoke(null)
                        : Activator.CreateInstance(_applySettingsType, nonPublic: true);
                    if (settings != null && settings.Count > 0)
                    {
                        ProjectJObjectOntoInstance(settings, applySettings, new List<string>(), 0);
                    }
                }
                catch (Exception ex) { Logger.Debug("InvokeReapply: ApplySettings build skipped: " + ex.Message); }
            }
            try
            {
                _applyPatternReapply.Invoke(null, new[] { patternInstanceObj, applySettings });
            }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        public PatternApplyResult ReapplyPattern(object patternInstance, JObject settings)
        {
            if (!EnsureProbed() || _applyPatternReapply == null)
                throw new InvalidOperationException("PatternEngine.ApplyPattern(PatternInstance, ApplySettings) not found");

            // ApplySettings is a pattern-internal type. We materialise it by reflection
            // (parameterless ctor) and best-effort-project the caller's JObject onto its
            // writable instance properties. Null/empty settings → pass null, which the
            // SDK treats as "use defaults" (preserving the historical behaviour).
            object applySettings = null;
            var unmapped = new List<string>();
            if (settings != null && settings.Count > 0)
            {
                applySettings = TryBuildApplySettings(settings, unmapped);
                if (applySettings == null)
                {
                    Logger.Warn("ReapplyPattern: failed to materialise ApplySettings; falling back to defaults. Unmapped keys: " + string.Join(", ", unmapped));
                }
                else if (unmapped.Count > 0)
                {
                    Logger.Info("ReapplyPattern: ApplySettings projected with " + unmapped.Count + " unmapped key(s): " + string.Join(", ", unmapped));
                }
            }

            try
            {
                _applyPatternReapply.Invoke(null, new[] { patternInstance, applySettings });
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }

            return new PatternApplyResult();
        }

        // Builds an ApplySettings instance and walks the JObject onto its writable
        // properties (case-insensitive). Best-effort: any miss is collected in
        // `unmapped` rather than thrown, so a partial projection still beats null.
        internal object TryBuildApplySettings(JObject settings, IList<string> unmapped)
        {
            if (_applySettingsType == null) return null;
            object instance;
            try
            {
                var ctor = _applySettingsType.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                {
                    // No parameterless ctor — try the activator anyway, in case the
                    // type exposes a non-public default.
                    instance = Activator.CreateInstance(_applySettingsType, nonPublic: true);
                }
                else
                {
                    instance = ctor.Invoke(null);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("ApplySettings ctor failed for " + _applySettingsType.FullName + ": " + ex.Message);
                return null;
            }

            ProjectJObjectOntoInstance(settings, instance, unmapped, depth: 0);
            return instance;
        }

        private static void ProjectJObjectOntoInstance(JObject src, object dst, IList<string> unmapped, int depth)
        {
            if (src == null || dst == null) return;
            // Defensive: pattern engine settings trees should not be deep. Avoid
            // pathological recursion if a nested type self-references.
            if (depth > 6) return;

            var type = dst.GetType();
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var kv in src)
            {
                if (!props.TryGetValue(kv.Key, out var prop))
                {
                    unmapped.Add(kv.Key);
                    continue;
                }
                try
                {
                    object value = CoerceJTokenToType(kv.Value, prop.PropertyType, unmapped, depth);
                    if (value == DBNull.Value) // sentinel: coercion declined
                    {
                        unmapped.Add(kv.Key);
                        continue;
                    }
                    prop.SetValue(dst, value, null);
                }
                catch (Exception ex)
                {
                    Logger.Debug("ApplySettings set " + kv.Key + " failed: " + ex.Message);
                    unmapped.Add(kv.Key);
                }
            }
        }

        private static object CoerceJTokenToType(JToken token, Type target, IList<string> unmapped, int depth)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var underlying = Nullable.GetUnderlyingType(target) ?? target;

            if (underlying == typeof(string)) return token.ToObject<string>();
            if (underlying == typeof(bool)) return token.ToObject<bool>();
            if (underlying == typeof(int)) return token.ToObject<int>();
            if (underlying == typeof(long)) return token.ToObject<long>();
            if (underlying == typeof(double)) return token.ToObject<double>();
            if (underlying == typeof(Guid)) return Guid.Parse(token.ToObject<string>() ?? string.Empty);
            if (underlying.IsEnum)
            {
                var s = token.ToObject<string>();
                if (string.IsNullOrEmpty(s))
                {
                    // numeric enum value
                    return Enum.ToObject(underlying, token.ToObject<int>());
                }
                return Enum.Parse(underlying, s, ignoreCase: true);
            }

            // Nested object → recurse (best-effort: requires parameterless ctor).
            if (token.Type == JTokenType.Object)
            {
                try
                {
                    var ctor = underlying.GetConstructor(Type.EmptyTypes);
                    var nested = ctor != null
                        ? ctor.Invoke(null)
                        : Activator.CreateInstance(underlying, nonPublic: true);
                    if (nested == null) return DBNull.Value;
                    ProjectJObjectOntoInstance((JObject)token, nested, unmapped, depth + 1);
                    return nested;
                }
                catch
                {
                    return DBNull.Value;
                }
            }

            // Last resort: let Newtonsoft figure it out (handles arrays/lists for known shapes).
            try { return token.ToObject(target); }
            catch { return DBNull.Value; }
        }
    }
}
