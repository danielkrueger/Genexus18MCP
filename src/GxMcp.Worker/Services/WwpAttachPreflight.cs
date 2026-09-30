using System;
using System.IO;
using System.Linq;
using System.Reflection;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Resolves the WorkWithPlus package types the direct-attach route depends on.
    /// Shared by the attach and by the projection step so both agree on which
    /// assembly is in play — the WWP package can be installed per GeneXus major,
    /// and a split-brain resolution shows up as "created a host, then could not
    /// project it".
    /// </summary>
    internal sealed class WwpPackageSurface
    {
        internal const string AssemblyName = "DVelop.Patterns.WorkWithPlus";
        internal const string PackageInterfaceTypeName =
            "DVelop.Patterns.WorkWithPlus.Helpers.PatternInstancePackageInterface";
        internal const string WorkWithPatternTypeName = "DVelop.Patterns.WorkWithPlus.WorkWithPattern";

        internal Assembly Assembly { get; private set; }
        internal Type PackageInterfaceType { get; private set; }
        internal Type WorkWithPatternType { get; private set; }
        /// <summary>Non-null when neither the loaded domain nor GX_PATH yielded the package.</summary>
        internal string LoadError { get; private set; }

        internal static WwpPackageSurface Resolve()
        {
            var surface = new WwpPackageSurface();
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
                if (assembly == null)
                {
                    try
                    {
                        var gxPath = Environment.GetEnvironmentVariable("GX_PATH") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
                        var dllPath = Path.Combine(gxPath, "Packages", "Patterns", AssemblyName, AssemblyName + ".dll");
                        if (File.Exists(dllPath)) assembly = Assembly.LoadFrom(dllPath);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug("WWP surface: load from GX_PATH failed: " + ex.Message);
                    }
                }
                if (assembly == null)
                {
                    surface.LoadError = AssemblyName + " is not loaded and was not found under GX_PATH\\Packages\\Patterns.";
                    return surface;
                }

                surface.Assembly = assembly;
                surface.PackageInterfaceType = assembly.GetType(PackageInterfaceTypeName, false);
                surface.WorkWithPatternType = assembly.GetType(WorkWithPatternTypeName, false);
                return surface;
            }
            catch (Exception ex)
            {
                surface.LoadError = ex.GetType().Name + ": " + ex.Message;
                return surface;
            }
        }
    }

    /// <summary>
    /// Fail-closed preflight for the WorkWithPlus direct-attach route used by
    /// WebPanel / WebComponent / SDPanel targets (issue #330).
    ///
    /// <c>PatternEngine.ApplyPattern</c>'s void overload is a silent no-op on every
    /// non-Transaction target — see <c>docs/sdk-probe/wwp-projection-discovery.md</c>,
    /// dead end #1 — so the <c>PatternInstancePackageInterface</c> pipeline is the
    /// only route by which a WebPanel can get the pattern. Each of its stages fails
    /// for a different reason, and the apply path used to collapse all of them into
    /// one sentence while <c>mode=diagnose</c> checked none of them and still
    /// answered "All pre-apply checks passed" for a target that could never apply.
    ///
    /// <see cref="PatternApplyService.DiagnosePattern"/> and
    /// <see cref="PatternApplyService.TryPackageInterfaceAttach"/> both run this
    /// resolver, so diagnosis cannot advertise a route the apply will not take.
    /// Every SDK touchpoint arrives as a parameter — no assembly loading, no KB
    /// access — so the unit suite exercises it against fake types without a
    /// licensed WorkWithPlus package.
    /// </summary>
    internal sealed class WwpAttachPreflight
    {
        internal const string StagePackageInterface = "packageInterface";
        internal const string StageCreateOverload = "createOverload";
        internal const string StageSetApplyOnSaveOverload = "setApplyOnSaveOverload";
        internal const string StageValidateAndSaveOverload = "validateAndSaveOverload";
        internal const string StageTemplate = "template";
        internal const string StageProjection = "projection";

        /// <summary>Stable machine-readable code surfaced as the envelope's error code.</summary>
        internal const string BlockedCode = "PatternAttachPreflightFailed";

        private readonly JArray _findings = new JArray();

        /// <summary>Members the attach reuses so it never re-resolves (and never disagrees).</summary>
        internal MethodInfo CreateMethod { get; private set; }
        internal object[] CreateArguments { get; private set; }
        internal int CreateByRefArgumentIndex { get; private set; }
        internal MethodInfo SetApplyOnSaveMethod { get; private set; }
        internal MethodInfo ValidateAndSaveMethod { get; private set; }

        /// <summary>True when every blocking stage resolved and the attach may proceed.</summary>
        internal bool CanAttach { get; private set; }
        /// <summary>First stage that blocked, or null when <see cref="CanAttach"/> is true.</summary>
        internal string BlockedStage { get; private set; }
        /// <summary>
        /// Every <c>CreatePatternInstanceWithTemplate</c> overload the installed
        /// package exposes. The attach puts this in a runtime-failure message
        /// because a create that throws or returns false needs the same evidence a
        /// resolution failure carries.
        /// </summary>
        internal string CreateCandidates { get; private set; }
        internal JArray Findings => _findings;

        /// <summary>
        /// Resolve every member the direct-attach route invokes for
        /// <paramref name="parentType"/>. <paramref name="resolvedTemplate"/> must
        /// already be a <c>WorkWithPlus for Web Template</c> name registered in this
        /// KB; the caller owns that lookup because it needs the template list anyway.
        /// </summary>
        internal static WwpAttachPreflight Run(
            Type packageInterfaceType,
            Type workWithPatternType,
            string parentType,
            object model,
            object parent,
            string resolvedTemplate)
        {
            var preflight = new WwpAttachPreflight();

            if (packageInterfaceType == null)
            {
                return preflight.Block(StagePackageInterface, "wwpPackageInterfaceMissing",
                    "The WorkWithPlus package does not expose " + WwpPackageSurface.PackageInterfaceTypeName +
                    ", the helper the IDE's Right-click → Apply Pattern route uses to bind a pattern instance to its target.",
                    "Install or update the WorkWithPlus pattern package in the active GeneXus installation " +
                    "(Packages\\Patterns\\WorkWithPlus), then rerun mode=diagnose.");
            }

            if (workWithPatternType == null)
            {
                return preflight.Block(StageProjection, "wwpProjectionMissing",
                    "The WorkWithPlus package does not expose " + WwpPackageSurface.WorkWithPatternTypeName +
                    ", so IPatternBuildProcess.UpdateParentObject — the step that projects the pattern onto the " +
                    parentType + "'s WebForm — cannot be reached.",
                    "Install or update the WorkWithPlus pattern package in the active GeneXus installation, then rerun mode=diagnose.");
            }
            if (workWithPatternType.GetConstructor(Type.EmptyTypes) == null)
            {
                return preflight.Block(StageProjection, "wwpProjectionCtorMissing",
                    WwpPackageSurface.WorkWithPatternTypeName + " has no public parameterless constructor, so the MCP cannot instantiate the build process headlessly.",
                    "Update the WorkWithPlus pattern package; the MCP projects the pattern by constructing " + WwpPackageSurface.WorkWithPatternTypeName + " directly.");
            }
            if (workWithPatternType.GetMethod("GetBuildProcess", Compatibility.SdkMemberProbe.Instance, null, Type.EmptyTypes, null) == null)
            {
                return preflight.Block(StageProjection, "wwpBuildProcessMissing",
                    WwpPackageSurface.WorkWithPatternTypeName + " does not expose GetBuildProcess(), so the projection step is unreachable. " +
                    "A host could be created but would never render on the " + parentType + ".",
                    "Update the WorkWithPlus pattern package; the MCP projects the pattern through " +
                    WwpPackageSurface.WorkWithPatternTypeName + ".GetBuildProcess().");
            }

            if (string.IsNullOrEmpty(resolvedTemplate))
            {
                return preflight.Block(StageTemplate, "wwpTemplateMissing",
                    "No 'WorkWithPlus for Web Template' object is available in this Knowledge Base, so CreatePatternInstanceWithTemplate " +
                    "has no template to seed the instance from and the " + parentType + " cannot receive the pattern.",
                    "Import or create a 'WorkWithPlus for Web Template' object in this KB, or set settings.template to one that already exists.");
            }

            var create = PatternApplyService.ResolveWwpCreateCall(
                packageInterfaceType, parentType, model, parent, resolvedTemplate,
                out var createArguments, out var byRefIndex, out var createCandidates, out var createError);
            // Recorded before the null check: an unresolvable call still has to be
            // able to say which overloads it rejected.
            preflight.CreateCandidates = createCandidates;
            if (create == null)
            {
                return preflight.Block(StageCreateOverload, "wwpCreateOverloadUnresolved",
                    "No usable CreatePatternInstanceWithTemplate overload for a " + parentType + " target: " +
                    (createError ?? "unknown") + " Candidates: " + createCandidates,
                    "Update the WorkWithPlus pattern package, or apply WorkWithPlus to a Transaction, which goes through the engine's own generation route instead of direct attach.");
            }
            preflight.CreateMethod = create;
            preflight.CreateArguments = createArguments;
            preflight.CreateByRefArgumentIndex = byRefIndex;

            // The remaining two helpers are single-signature in every shipped WWP
            // build, but they are resolved through the same scorer so an unexpected
            // overload cannot turn into an AmbiguousMatchException mid-attach.
            var singleArgument = new[] { parent };
            var setApplyOnSave = PatternApplyService.ResolveCompatibleStaticOverload(
                packageInterfaceType, "SetPatternApplyOnSave", singleArgument, -1, typeof(bool),
                out var setApplyCandidates, out var setApplyError);
            if (setApplyOnSave == null)
            {
                return preflight.Block(StageSetApplyOnSaveOverload, "wwpSetApplyOnSaveUnresolved",
                    "No usable SetPatternApplyOnSave overload: " + (setApplyError ?? "unknown") + " Candidates: " + setApplyCandidates,
                    "Update the WorkWithPlus pattern package, then rerun mode=diagnose.");
            }
            preflight.SetApplyOnSaveMethod = setApplyOnSave;

            var validateAndSave = PatternApplyService.ResolveCompatibleStaticOverload(
                packageInterfaceType, "ValidateAndSave", singleArgument, -1, typeof(bool),
                out var validateCandidates, out var validateError);
            if (validateAndSave == null)
            {
                return preflight.Block(StageValidateAndSaveOverload, "wwpValidateAndSaveUnresolved",
                    "No usable ValidateAndSave overload: " + (validateError ?? "unknown") + " Candidates: " + validateCandidates,
                    "Update the WorkWithPlus pattern package, then rerun mode=diagnose.");
            }
            preflight.ValidateAndSaveMethod = validateAndSave;

            preflight.CanAttach = true;
            return preflight;
        }

        private WwpAttachPreflight Block(string stage, string reason, string detail, string remediation)
        {
            BlockedStage = stage;
            CanAttach = false;
            _findings.Add(new JObject
            {
                ["reason"] = reason,
                ["severity"] = "critical",
                ["stage"] = stage,
                ["detail"] = detail,
                ["remediation"] = remediation
            });
            return this;
        }
    }
}
