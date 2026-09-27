using System;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Friction 2026-05-26 — preserves the IDE's "Apply this pattern on save"
    /// checkbox after the MCP edits a WorkWithPlus PatternInstance.
    ///
    /// The IDE invokes
    /// <c>DVelop.Patterns.WorkWithPlus.Helpers.PatternInstancePackageInterface.SetPatternApplyOnSave(host)</c>
    /// to flip the flag back on; without it, every raw <c>obj.Save(prefs)</c>
    /// path the MCP uses clears the flag and the next IDE refresh shows the
    /// checkbox unchecked. This helper is reflection-only over the WWP package
    /// so the worker still builds and runs in environments where WWP is not
    /// installed (returns false, logs at debug).
    /// </summary>
    internal static class WwpApplyOnSaveHelper
    {
        private static long _invocationCount;

        /// <summary>
        /// Test seam: number of times <see cref="TryEnable"/> has invoked the
        /// WWP SDK statics in this process. Lets unit/integration tests verify
        /// the call happened without needing a live WWP install.
        /// </summary>
        internal static long InvocationCount => System.Threading.Interlocked.Read(ref _invocationCount);

        /// <summary>
        /// Invokes <c>SetPatternApplyOnSave(host)</c> and persists the change.
        /// Returns true when the SDK method was found and ran without throwing.
        /// Best-effort; any miss or exception is logged and returns false
        /// instead of bubbling.
        /// </summary>
        internal static bool TryEnable(KBObject host)
        {
            if (host == null) return false;
            try
            {
                var wwpAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "DVelop.Patterns.WorkWithPlus", StringComparison.OrdinalIgnoreCase));
                if (wwpAsm == null) { Logger.Debug("[APPLY-ON-SAVE] DVelop.Patterns.WorkWithPlus not loaded"); return false; }
                var ifaceType = wwpAsm.GetType("DVelop.Patterns.WorkWithPlus.Helpers.PatternInstancePackageInterface", false);
                if (ifaceType == null) { Logger.Debug("[APPLY-ON-SAVE] PatternInstancePackageInterface not found"); return false; }
                var setApplyMethod = ifaceType.GetMethod("SetPatternApplyOnSave", Compatibility.SdkMemberProbe.Static);
                if (setApplyMethod == null) { Logger.Debug("[APPLY-ON-SAVE] SetPatternApplyOnSave method not found"); return false; }
                setApplyMethod.Invoke(null, new object[] { host });
                System.Threading.Interlocked.Increment(ref _invocationCount);
                Logger.Info("[APPLY-ON-SAVE] SetPatternApplyOnSave invoked on host '" + host.Name + "'");

                // Persist the change so the IDE sees the flag flip across the
                // next refresh — the SDK call mutates in-memory state only.
                try
                {
                    var prefs = new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
                    {
                        ForceSave = true,
                        ForceSaveDefaultParts = true,
                        SkipValidation = true
                    };
                    host.Save(prefs);
                }
                catch (Exception saveEx)
                {
                    Logger.Info("[APPLY-ON-SAVE] host.Save threw: " + saveEx.Message + " — trying EnsureSave(true)");
                    try { host.EnsureSave(true); } catch (Exception ex2) { Logger.Info("[APPLY-ON-SAVE] EnsureSave fallback also failed: " + ex2.Message); }
                }
                return true;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                Logger.Info("[APPLY-ON-SAVE] SetPatternApplyOnSave threw: " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Info("[APPLY-ON-SAVE] reflection failed: " + ex.Message);
                return false;
            }
        }
    }
}
