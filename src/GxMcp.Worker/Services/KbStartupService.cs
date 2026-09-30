using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave-3 item — IDE "Set As Startup Object" / get-startup parity.
    ///
    /// Wraps the SDK's startup-object resolution. <see cref="GetStartup"/>
    /// returns the active Environment's <c>StartupObject</c> and the
    /// fall-back <c>DefaultObject</c>, mirroring what
    /// <see cref="KbService.GetLauncherObjectName"/> already uses on F5.
    /// <see cref="SetStartup"/> verifies the object exists and then writes
    /// the active Environment's <c>StartupObject</c> via the SDK's
    /// <c>SetPropertyValue</c> (probed by reflection across SDK shapes).
    ///
    /// All SDK access is funneled through <see cref="IEnvPropertyStore"/>
    /// so unit tests can run against an in-memory implementation without
    /// touching the GeneXus SDK at all.
    /// </summary>
    public class KbStartupService
    {
        public interface IEnvPropertyStore
        {
            string Get(string propertyName);
            // Returns true on success; false when the SDK rejects the write
            // (unknown property, type mismatch, env handle missing, etc.).
            bool Set(string propertyName, string value);
        }

        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IEnvPropertyStore _store;

        public KbStartupService(KbService kbService, ObjectService objectService)
            : this(kbService, objectService, new SdkEnvPropertyStore(kbService))
        {
        }

        public KbStartupService(KbService kbService, ObjectService objectService, IEnvPropertyStore store)
        {
            _kbService = kbService;
            _objectService = objectService;
            _store = store;
        }

        public string GetStartup()
        {
            try
            {
                string startup = _store.Get("StartupObject");
                string fallback = _store.Get("DefaultObject");
                string effective = _kbService?.GetLauncherObjectName();
                return McpResponse.Ok(
                    code: "KbStartupRetrieved",
                    result: new JObject
                    {
                        ["startupObject"] = startup ?? string.Empty,
                        ["defaultObject"] = fallback ?? string.Empty,
                        ["effective"] = effective ?? string.Empty,
                        ["hint"] = string.IsNullOrEmpty(startup)
                            ? "StartupObject not set; effective launcher resolves through DefaultObject / first Main-tagged object."
                            : null
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "GetStartupFailed",
                    message: ex.Message,
                    hint: "Verify the active KB is open and the SDK environment is initialized.",
                    nextSteps: new JArray { McpResponse.NextStep("genexus_whoami", null, "Check KB and environment state.") });
            }
        }

        public string SetStartup(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                return McpResponse.Err(
                    code: "MissingName",
                    message: "Missing 'name'.",
                    hint: "Pass the object name to set as startup.",
                    nextSteps: new JArray { McpResponse.NextStep("genexus_list_objects", new JObject { ["type"] = "WebPanel" }, "List available objects.") });

            try
            {
                var obj = _objectService?.FindObject(objectName, null);
                if (obj == null)
                {
                    return McpResponse.Err(
                        code: "NotFound",
                        message: $"Object '{objectName}' not found in KB.",
                        hint: "Verify the object name is spelled correctly.",
                        nextSteps: new JArray { McpResponse.NextStep("genexus_inspect", new JObject { ["name"] = objectName }, "Inspect to confirm the object name.") },
                        target: objectName);
                }

                string previous = null;
                try { previous = _store.Get("StartupObject"); } catch { }

                bool ok = _store.Set("StartupObject", obj.Name);
                if (!ok)
                {
                    return McpResponse.Err(
                        code: "SetFailed",
                        message: "SDK refused to write StartupObject.",
                        hint: "Verify the active Environment is loaded and that the SDK shape exposes StartupObject as a writable env property.",
                        nextSteps: new JArray { McpResponse.NextStep("genexus_whoami", null, "Check environment state.") },
                        target: obj.Name);
                }

                return McpResponse.Ok(
                    target: obj.Name,
                    code: "KbStartupCompleted",
                    result: new JObject
                    {
                        ["startupObject"] = obj.Name,
                        ["previousStartupObject"] = previous ?? string.Empty
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "SetStartupFailed",
                    message: ex.Message,
                    hint: "An unexpected error occurred setting the startup object.",
                    nextSteps: new JArray { McpResponse.NextStep("genexus_whoami", null, "Check KB state.") });
            }
        }

        // SDK-backed store. Probes the same Environment shapes that
        // KbService.GetActiveEnvironment + GetLauncherObjectName already
        // know about; writes through SetPropertyValue/SetPropertyValueString
        // by reflection so we work across SDK major versions.
        internal sealed class SdkEnvPropertyStore : IEnvPropertyStore
        {
            private readonly KbService _kbService;
            public SdkEnvPropertyStore(KbService kbService) { _kbService = kbService; }

            public string Get(string propertyName)
            {
                dynamic kb = _kbService?.GetKB();
                if (kb == null) return null;

                if (string.Equals(propertyName, "StartupObject", StringComparison.OrdinalIgnoreCase))
                {
                    try { var v = kb.DefaultStartupObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                }
                if (string.Equals(propertyName, "DefaultObject", StringComparison.OrdinalIgnoreCase))
                {
                    try { var v = kb.UserInterface?.MainObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                    try { var v = kb.MainObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                }

                object envContainer = ResolveEnvContainer(kb);
                if (envContainer == null) return null;
                try
                {
                    object v = TryInvokeGetPropertyValue(envContainer, propertyName);
                    return v?.ToString();
                }
                catch { return null; }
            }

            public bool Set(string propertyName, string value)
            {
                dynamic kb = _kbService?.GetKB();
                if (kb == null) return false;

                object envContainer = ResolveEnvContainer(kb);
                if (envContainer == null) return false;

                if (TryInvokeSetPropertyValueString(envContainer, propertyName, value)) return true;
                if (TryInvokeSetPropertyValue(envContainer, propertyName, value)) return true;
                return false;
            }

            private static object ResolveEnvContainer(dynamic kb)
            {
                try { object v = kb.Environment; if (v != null) return v; } catch { }
                try { object v = kb.UserInterface?.ActiveEnvironment; if (v != null) return v; } catch { }
                try { object v = kb.DesignModel?.Environment; if (v != null) return v; } catch { }
                try { object v = kb.ActiveModel; if (v != null) return v; } catch { }
                return null;
            }

            // All three go through SdkReflection. The previous get helper had no
            // try/catch at all: a throwing GetPropertyValue escaped a method whose name
            // promised it would not, and the caller's guard was the only thing between
            // a bad property read and a failed KB startup.

            private static object TryInvokeGetPropertyValue(object target, string propName)
            {
                return Helpers.SdkReflection.TryGetPropertyBagValue(target, propName);
            }

            private static bool TryInvokeSetPropertyValueString(object target, string propName, string value)
            {
                if (target == null) return false;
                try
                {
                    var mi = FindPropertyBagSetter(target, "SetPropertyValueString", typeof(string), typeof(string));
                    if (mi == null) return false;
                    mi.Invoke(target, new object[] { propName, value });
                    return true;
                }
                catch (Exception ex) { Logger.Debug("[KbStartup] SetPropertyValueString failed: " + ex.Message); return false; }
            }

            private static bool TryInvokeSetPropertyValue(object target, string propName, object value)
            {
                if (target == null) return false;
                try
                {
                    var mi = FindPropertyBagSetter(target, "SetPropertyValue", typeof(string), typeof(object));
                    if (mi == null) return false;
                    mi.Invoke(target, new object[] { propName, value });
                    return true;
                }
                catch (Exception ex) { Logger.Debug("[KbStartup] SetPropertyValue failed: " + ex.Message); return false; }
            }

            /// <summary>
            /// Locates a two-argument property-bag setter, checking BOTH parameter
            /// types. The previous setter matched only the first, so on a major that
            /// also declares <c>SetPropertyValue(string, int)</c> it could bind the
            /// wrong overload and fail at invoke time with a confusing cast error.
            /// </summary>
            private static System.Reflection.MethodInfo FindPropertyBagSetter(
                object target, string name, System.Type first, System.Type second)
            {
                return target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == name
                                      && m.GetParameters().Length == 2
                                      && m.GetParameters()[0].ParameterType == first
                                      && m.GetParameters()[1].ParameterType == second);
            }
        }
    }
}
