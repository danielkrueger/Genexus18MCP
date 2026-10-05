using System;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Static helper that invokes the WorkWithPlus pattern build process'
    /// <c>UpdateParentObject</c> step — the SDK lifecycle hook that projects a
    /// PatternInstance onto the bound KBObject's WebForm. Discovered via F17
    /// (see <c>docs/sdk-probe/wwp-projection-discovery.md</c>).
    ///
    /// Lifted out of <see cref="PatternApplyService"/> so <see cref="WriteService"/>
    /// can invoke it after a successful PatternInstance edit without taking a
    /// circular dependency on PatternApplyService.
    /// </summary>
    internal static class WwpProjectionHelper
    {
        internal sealed class ProjectionResult
        {
            internal bool LifecycleAttempted;
            internal bool ShouldBuild;
            internal bool BeforeStartBuild;
            internal bool AfterImportResources;
            internal bool UpdateParentObject;
            internal bool AfterEndBuild;
            internal bool ParentSaved;
            internal string Failure;

            internal bool LifecycleExecuted => LifecycleAttempted
                && ShouldBuild
                && BeforeStartBuild
                && AfterImportResources
                && UpdateParentObject
                && AfterEndBuild
                && ParentSaved;
        }

        /// <summary>
        /// Project the PatternInstance on <paramref name="host"/> onto
        /// <paramref name="parent"/>'s WebForm via the WWP build process.
        /// Saves the parent with ForceSave + SkipValidation so the projected
        /// WebForm persists even if it would fail WebPanel-level semantic checks.
        ///
        /// Errors are logged and swallowed — the edit that triggered this call
        /// already succeeded; projection is best-effort.
        /// </summary>
        public static bool TryProjectHostOntoParent(KBObject parent, KBObject host)
        {
            return TryProjectHostOntoParent(parent, host, out _);
        }

        public static bool TryProjectHostOntoParent(KBObject parent, KBObject host,
            out ProjectionResult result)
        {
            result = new ProjectionResult();
            if (parent == null || host == null)
            {
                result.Failure = parent == null ? "parent KBObject is null" : "host KBObject is null";
                return false;
            }
            try
            {
                var wwpAsm = WwpPackageSurface.Resolve().Assembly;
                if (wwpAsm == null)
                {
                    result.Failure = WwpPackageSurface.AssemblyName + " is not loaded and was not found under GX_PATH\\Packages\\Patterns.";
                    Logger.Warn("[WWP-PROJECT] " + result.Failure);
                    return false;
                }

                var workWithPatternType = wwpAsm.GetType(WwpPackageSurface.WorkWithPatternTypeName, false);
                if (workWithPatternType == null)
                {
                    result.Failure = WwpPackageSurface.WorkWithPatternTypeName + " not found in " + wwpAsm.GetName().Name + ".";
                    Logger.Warn("[WWP-PROJECT] " + result.Failure);
                    return false;
                }

                object impl;
                try { impl = Activator.CreateInstance(workWithPatternType); }
                catch (Exception ex)
                {
                    result.Failure = WwpPackageSurface.WorkWithPatternTypeName + " ctor failed: " + ex.GetType().Name + ": " + ex.Message;
                    Logger.Warn("[WWP-PROJECT] " + result.Failure);
                    return false;
                }
                if (impl == null)
                {
                    result.Failure = WwpPackageSurface.WorkWithPatternTypeName + " ctor returned null.";
                    Logger.Warn("[WWP-PROJECT] " + result.Failure);
                    return false;
                }

                try
                {
                    var initMethod = workWithPatternType.GetMethod("Initialize",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    initMethod?.Invoke(impl, null);
                }
                catch (Exception ex) { Logger.Debug("[WWP-PROJECT] Initialize skipped: " + ex.Message); }

                var getBuildProcess = workWithPatternType.GetMethod("GetBuildProcess",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getBuildProcess == null)
                {
                    result.Failure = WwpPackageSurface.WorkWithPatternTypeName + " does not expose GetBuildProcess().";
                    Logger.Warn("[WWP-PROJECT] " + result.Failure);
                    return false;
                }

                object buildProcess;
                try { buildProcess = getBuildProcess.Invoke(impl, null); }
                catch (TargetInvocationException tie)
                {
                    result.Failure = "GetBuildProcess() threw: " + (tie.InnerException?.GetType().Name) + ": " + tie.InnerException?.Message;
                    Logger.Warn("[WWP-PROJECT] " + result.Failure);
                    return false;
                }
                if (buildProcess == null)
                {
                    result.Failure = "GetBuildProcess() returned null.";
                    Logger.Warn("[WWP-PROJECT] " + result.Failure);
                    return false;
                }

                RefreshHostStateForProjection(host);

                var updateParent = buildProcess.GetType().GetMethod("UpdateParentObject",
                    BindingFlags.Public | BindingFlags.Instance);
                if (updateParent == null)
                {
                    result.Failure = "IPatternBuildProcess.UpdateParentObject is missing on " + buildProcess.GetType().FullName + ".";
                    Logger.Warn("[WWP-PROJECT] " + result.Failure);
                    return false;
                }

                // F19: Run the FULL IPatternBuildProcess lifecycle the IDE uses, not
                // just UpdateParentObject. Most hooks tolerate missing context (best-
                // effort wrappers), but BeforeStartBuild / AfterEndBuild establish
                // engine state that UpdateParentObject depends on for richer templates.
                //   ShouldBuild → BeforeStartBuild → AfterImportResources →
                //   BeforeGenerateObjects → UpdateParentObject → AfterSaveObjects →
                //   AfterEndBuild
                Logger.Info("[WWP-PROJECT] Running full IPatternBuildProcess lifecycle on " +
                    buildProcess.GetType().FullName + " for host=" + host.Name);

                result.LifecycleAttempted = true;
                result.ShouldBuild = TryInvokeBP(buildProcess, "ShouldBuild", new[] { host });
                result.BeforeStartBuild = result.ShouldBuild
                    && TryInvokeBP(buildProcess, "BeforeStartBuild", new[] { host });
                result.AfterImportResources = result.BeforeStartBuild
                    && TryInvokeBP(buildProcess, "AfterImportResources", new[] { host });
                if (!result.AfterImportResources)
                {
                    result.Failure = "A required WorkWithPlus build-process callback failed before projection.";
                    return false;
                }
                // BeforeGenerateObjects takes (PatternInstance, IBaseCollection<PatternObject>).
                // We don't have the second arg cheaply; skip — it's mostly used to filter
                // which objects to build, not required for the projection step.

                updateParent.Invoke(buildProcess, new object[] { parent, host });
                result.UpdateParentObject = true;
                Logger.Info("[WWP-PROJECT] UpdateParentObject returned successfully");

                // AfterSaveObjects takes (PatternInstance, InstanceObjects); we don't have
                // a real InstanceObjects collection. Same for BeforeSaveObjects. Skip
                // them rather than pass nulls and risk NRE inside the SDK.

                result.AfterEndBuild = TryInvokeBP(buildProcess, "AfterEndBuild", new[] { host });
                if (!result.AfterEndBuild)
                {
                    result.Failure = "The WorkWithPlus AfterEndBuild callback failed after projection.";
                    return false;
                }

                try
                {
                    var prefs = new global::Artech.Architecture.Common.Objects.KBObjectSavePreferences
                    {
                        ForceSave = true,
                        ForceSaveDefaultParts = true,
                        SkipValidation = true
                    };
                    parent.Save(prefs);
                    // Every caller of this helper marks only the WWP instance and reports
                    // the parent separately, but the projection regenerates the parent
                    // WebForm here, and a WebForm is itself a build target. Without this
                    // mark the next build takes the compile-only fast path and ships a
                    // stale generated class.
                    WriteService.NotePerTargetWrite(parent.Name);
                    result.ParentSaved = true;
                    Logger.Info("[WWP-PROJECT] Saved parent '" + parent.Name + "' (ForceSave+SkipValidation).");
                }
                catch (Exception saveEx)
                {
                    Logger.Info("[WWP-PROJECT] ForceSave parent threw: " + saveEx.Message + " — falling back to EnsureSave.");
                    try
                    {
                        parent.EnsureSave(true);
                        // The fallback path is a real save, not a lesser one: it persists
                        // the same parent, so it owes the same mark.
                        WriteService.NotePerTargetWrite(parent.Name);
                        result.ParentSaved = true;
                    }
                    catch (Exception ex2)
                    {
                        result.Failure = "Both ForceSave and EnsureSave failed: " + ex2.Message;
                        Logger.Info("[WWP-PROJECT] EnsureSave fallback failed: " + ex2.Message);
                    }
                }
                return result.LifecycleExecuted;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                result.Failure = inner.GetType().Name + ": " + inner.Message;
                Logger.Warn("[WWP-PROJECT] UpdateParentObject threw: " + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
            catch (Exception ex)
            {
                result.Failure = ex.GetType().Name + ": " + ex.Message;
                Logger.Warn("[WWP-PROJECT] failed: " + ex.Message);
                return false;
            }
        }

        private static void RefreshHostStateForProjection(KBObject host)
        {
            if (host == null) return;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (string methodName in new[] { "RefreshDefaultDependentParts", "Reload", "Refresh", "Reset" })
            {
                try
                {
                    var method = host.GetType().GetMethod(methodName, flags, null, Type.EmptyTypes, null);
                    if (method == null) continue;
                    method.Invoke(host, null);
                    Logger.Info("[WWP-PROJECT] Refreshed host state via " + methodName + " before projection.");
                    return;
                }
                catch (TargetInvocationException tie)
                {
                    Logger.Debug("[WWP-PROJECT] Host " + methodName + " skipped: " + (tie.InnerException?.Message ?? tie.Message));
                }
                catch (Exception ex)
                {
                    Logger.Debug("[WWP-PROJECT] Host " + methodName + " skipped: " + ex.Message);
                }
            }
            Logger.Debug("[WWP-PROJECT] No host refresh method was available before projection.");
        }

        // Best-effort invoke for IPatternBuildProcess lifecycle hooks that take
        // a single (PatternInstance) argument. Logs and swallows errors — these
        // are advisory in our headless context and missing services in the SDK
        // shouldn't fail the projection.
        private static bool TryInvokeBP(object buildProcess, string methodName, object[] args)
        {
            try
            {
                var m = buildProcess.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                if (m == null) { Logger.Debug("[WWP-PROJECT] " + methodName + " not found"); return false; }
                object value = m.Invoke(buildProcess, args);
                if (m.ReturnType == typeof(bool) && value is bool boolValue && !boolValue)
                {
                    Logger.Debug("[WWP-PROJECT] " + methodName + " returned False");
                    return false;
                }
                Logger.Debug("[WWP-PROJECT] " + methodName + " ok");
                return true;
            }
            catch (TargetInvocationException tie)
            {
                Logger.Debug("[WWP-PROJECT] " + methodName + " threw: " + (tie.InnerException?.GetType().Name ?? "") + ": " + (tie.InnerException?.Message ?? ""));
                return false;
            }
            catch (Exception ex)
            {
                Logger.Debug("[WWP-PROJECT] " + methodName + " reflection error: " + ex.Message);
                return false;
            }
        }

        // Only these types can parent a WorkWithPlus instance. A Folder, Table, Module,
        // Attribute or Domain may share the bare name and has no WebForm (#413).
        private static readonly string[] ParentTypeNames = { "Transaction", "WebPanel" };

        internal static bool IsAcceptedParentType(string typeName)
        {
            foreach (var accepted in ParentTypeNames)
                if (string.Equals(accepted, typeName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Names to try for the host's parent, best first: the root <c>transaction</c>
        /// attribute (<c>&lt;type GUID&gt;-&lt;Name&gt;</c>), then the
        /// <c>WorkWithPlus&lt;X&gt;</c> convention. A WebPanel instance has no such attribute.
        /// </summary>
        internal static System.Collections.Generic.List<string> ParentNameCandidates(string hostName, string instanceXml)
        {
            var names = new System.Collections.Generic.List<string>();
            string fromInstance = null;
            if (!string.IsNullOrWhiteSpace(instanceXml))
            {
                try
                {
                    string value = System.Xml.Linq.XDocument.Parse(instanceXml).Root?.Attribute("transaction")?.Value;
                    // "<36-char GUID>-<Name>"
                    if (value != null && value.Length > 37 && value[36] == '-' && Guid.TryParse(value.Substring(0, 36), out _))
                        fromInstance = value.Substring(37);
                }
                catch (System.Xml.XmlException) { }
            }
            if (!string.IsNullOrEmpty(fromInstance)) names.Add(fromInstance);

            const string prefix = "WorkWithPlus";
            if (hostName != null && hostName.StartsWith(prefix, StringComparison.Ordinal) && hostName.Length > prefix.Length)
            {
                string byConvention = hostName.Substring(prefix.Length);
                if (!names.Contains(byConvention)) names.Add(byConvention);
            }
            return names;
        }

        /// <summary>
        /// Resolve the parent KBObject for a WorkWithPlus host by type (Transaction, then
        /// WebPanel) and never by bare name alone, so a same-named Folder/Table is not taken.
        /// </summary>
        public static KBObject ResolveHostParent(KBObject host, ObjectService objectService, string instanceXml = null)
        {
            if (host == null || objectService == null) return null;
            foreach (string name in ParentNameCandidates(host.Name, instanceXml))
            {
                foreach (string type in ParentTypeNames)
                {
                    try
                    {
                        var parent = objectService.FindObject(name, type);
                        if (parent != null && IsAcceptedParentType(parent.TypeDescriptor?.Name)) return parent;
                    }
                    catch { }
                }
            }
            return null;
        }
    }
}
