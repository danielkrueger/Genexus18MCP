using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Visual-surface resolution: loading, reflecting, parsing, scoring and
    // source-path persistence of layout/WebForm XML. Extracted from
    // LayoutService.cs (plan TECHDEBT-03). Pure move, no logic changes — see
    // plans/README.md TECHDEBT-03.
    public partial class LayoutService
    {
        private static LayoutContextResult LoadVisualContext(KBObject obj, string target, VisualSurface preferredSurface)
        {
            if (preferredSurface == VisualSurface.Any || preferredSurface == VisualSurface.Report)
            {
                var reportPart = PartAccessor.GetPart(obj, "Layout");
                if (reportPart != null)
                {
                    if (ReportLayoutHelper.IsReportPart(reportPart) != null)
                    {
                        string xml = ReportLayoutHelper.ReadLayout(reportPart);
                        var parsed = ParseVisualXml(xml, target, "Procedure Layout XML not found", "The Procedure does not expose a valid ReportPart layout.");
                        if (parsed.Document != null)
                        {
                            return LayoutContextResult.FromReport(reportPart, parsed.Document);
                        }

                        if (preferredSurface == VisualSurface.Report)
                        {
                            return LayoutContextResult.FromError(parsed.Error);
                        }
                    }
                    else
                    {
                        Logger.Debug($"Part 'Layout' found for {obj.Name} but rejected by ReportLayoutHelper (Type: {reportPart.TypeDescriptor?.Name ?? "Unknown"}, GUID: {reportPart.Type})");
                    }
                }
            }

            if (preferredSurface == VisualSurface.Any || preferredSurface == VisualSurface.WebForm)
            {
                var webFormPart = WebFormXmlHelper.GetWebFormPart(obj);
                if (webFormPart != null)
                {
                    string xml = WebFormXmlHelper.ReadEditableXml(obj);
                    var parsed = ParseVisualXml(xml, target, "Layout/WebForm XML not found", "The object does not expose editable visual XML.");
                    if (parsed.Document != null)
                    {
                        return LayoutContextResult.FromWebForm(webFormPart, parsed.Document);
                    }

                    if (preferredSurface == VisualSurface.WebForm)
                    {
                        return LayoutContextResult.FromError(parsed.Error);
                    }
                }
            }

            if (preferredSurface == VisualSurface.Any || preferredSurface == VisualSurface.LayoutSource)
            {
                var layoutResult = TryLoadXmlFromPart(obj, target, "Layout");
                if (layoutResult != null)
                {
                    return layoutResult;
                }

                var patternVirtualResult = TryLoadXmlFromPart(obj, target, "PatternVirtual");
                if (patternVirtualResult != null)
                {
                    return patternVirtualResult;
                }

                if (preferredSurface == VisualSurface.LayoutSource)
                {
                    return LayoutContextResult.FromError(
                        Models.McpResponse.Err(
                            code: "LayoutPartNotFound",
                            message: "Layout part not found.",
                            hint: "The object does not expose a textual Layout part for visual editing.",
                            nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Lists available visual surfaces for this object.")),
                            target: target));
                }
            }

            return LayoutContextResult.FromError(
                Models.McpResponse.Err(
                    code: "VisualPartNotFound",
                    message: "Visual part not found.",
                    hint: "The object does not expose a supported visual part (WebForm or Layout source XML).",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Lists available visual surfaces for this object.")),
                    target: target));
        }

        private static LayoutContextResult TryLoadXmlFromPart(KBObject obj, string target, string partName)
        {
            var part = PartAccessor.GetPart(obj, partName);
            if (part == null) return null;

            if (part is ISource sourcePart)
            {
                string xml = sourcePart.Source;
                var parsed = ParseVisualXml(xml, target, $"{partName} source is empty", $"The object {partName} source is empty or not available.");
                if (parsed.Document != null)
                {
                    return LayoutContextResult.FromLayoutSource(partName, sourcePart, parsed.Document);
                }

                return LayoutContextResult.FromError(parsed.Error);
            }

            var reflectiveXml = TryExtractXmlFromPartMembers(part, target, partName);
            if (reflectiveXml != null)
            {
                return reflectiveXml;
            }

            string serializedXml;
            try
            {
                serializedXml = part.SerializeToXml();
            }
            catch
            {
                return null;
            }

            var parsedXml = ParseVisualXml(serializedXml, target, $"{partName} XML is empty", $"The object {partName} XML is empty or not available.");
            if (parsedXml.Document == null)
            {
                return LayoutContextResult.FromError(parsedXml.Error);
            }

            return LayoutContextResult.FromPartXml(partName, part, parsedXml.Document);
        }

        private static LayoutContextResult TryExtractXmlFromPartMembers(KBObjectPart part, string target, string partName)
        {
            var candidates = CollectXmlCandidates(part, includeNonPublic: true, includeNested: true);

            var best = candidates
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Document.Descendants().Count())
                .FirstOrDefault();

            if (best == null)
            {
                return null;
            }

            string memberName = best.Property != null ? best.Property.Name : best.GetterMethod?.Name;
            bool writable = best.Property != null && best.Property.CanWrite;
            return LayoutContextResult.FromPartMemberXml(partName, part, best.Document, memberName, best.SourcePath, writable);
        }

        private static List<MemberXmlCandidate> CollectXmlCandidates(KBObjectPart part, bool includeNonPublic, bool includeNested)
        {
            var candidates = new List<MemberXmlCandidate>();
            var visited = new HashSet<object>(ReferenceObjectComparer.Instance);
            CollectXmlCandidatesFromObject(
                part,
                part.GetType().Name,
                depth: 0,
                maxDepth: includeNested ? 2 : 0,
                includeNonPublic: includeNonPublic,
                candidates: candidates,
                visited: visited);

            return candidates;
        }

        private static void CollectXmlCandidatesFromObject(
            object instance,
            string sourcePath,
            int depth,
            int maxDepth,
            bool includeNonPublic,
            List<MemberXmlCandidate> candidates,
            HashSet<object> visited)
        {
            if (instance == null || depth > maxDepth) return;
            if (!visited.Add(instance)) return;

            var flags = BindingFlags.Public | BindingFlags.Instance;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            var type = instance.GetType();

            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead) continue;

                bool accessorPublic = prop.GetMethod != null && prop.GetMethod.IsPublic;

                if (prop.PropertyType == typeof(string) && LooksLikeXmlCarrierName(prop.Name))
                {
                    string value;
                    try { value = prop.GetValue(instance) as string; } catch { value = null; }

                    var parsed = TryParseCandidateXml(value);
                    if (parsed != null)
                    {
                        string candidatePath = sourcePath + "." + prop.Name;
                        candidates.Add(new MemberXmlCandidate
                        {
                            Xml = value,
                            Document = parsed,
                            Score = ScoreVisualXml(parsed) + ScoreSourcePath(candidatePath),
                            Property = prop,
                            MemberName = prop.Name,
                            SourcePath = candidatePath,
                            Depth = depth,
                            MemberKind = accessorPublic ? "property" : "property_nonpublic",
                            MemberWritable = prop.SetMethod != null && (prop.SetMethod.IsPublic || includeNonPublic)
                        });
                    }
                }

                if (depth >= maxDepth) continue;
                if (!ShouldTraverseMember(prop.Name, prop.PropertyType)) continue;

                object nested;
                try { nested = prop.GetValue(instance); } catch { nested = null; }
                if (nested == null) continue;

                CollectXmlCandidatesFromObject(
                    nested,
                    sourcePath + "." + prop.Name,
                    depth + 1,
                    maxDepth,
                    includeNonPublic,
                    candidates,
                    visited);
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.GetParameters().Length != 0) continue;
                if (method.IsSpecialName) continue;
                if (method.ReturnType != typeof(string)) continue;
                if (!LooksLikeXmlCarrierName(method.Name)) continue;
                if (string.Equals(method.Name, "ToString", StringComparison.Ordinal)) continue;

                string value;
                try { value = method.Invoke(instance, null) as string; } catch { value = null; }

                var parsed = TryParseCandidateXml(value);
                if (parsed == null) continue;

                string candidatePath = sourcePath + "." + method.Name + "()";
                candidates.Add(new MemberXmlCandidate
                {
                    Xml = value,
                    Document = parsed,
                    Score = ScoreVisualXml(parsed) + ScoreSourcePath(candidatePath),
                    GetterMethod = method,
                    MemberName = method.Name,
                    SourcePath = candidatePath,
                    Depth = depth,
                    MemberKind = method.IsPublic ? "method" : "method_nonpublic",
                    MemberWritable = false
                });
            }
        }

        private static bool LooksLikeXmlCarrierName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("xml") || n.Contains("layout") || n.Contains("source") || n.Contains("metadata") || n.Contains("content") || n.Contains("form") || n.Contains("control");
        }

        private static bool ShouldTraverseMember(string memberName, Type memberType)
        {
            if (memberType == null) return false;
            if (memberType == typeof(string)) return false;
            if (memberType.IsPrimitive || memberType.IsEnum) return false;
            if (typeof(IEnumerable).IsAssignableFrom(memberType) && memberType != typeof(byte[])) return false;

            string typeName = memberType.FullName ?? memberType.Name ?? string.Empty;
            string lowerType = typeName.ToLowerInvariant();
            string lowerName = (memberName ?? string.Empty).ToLowerInvariant();

            bool strongHint =
                lowerName.Contains("layout") ||
                lowerName.Contains("form") ||
                lowerName.Contains("xml") ||
                lowerName.Contains("control") ||
                lowerName.Contains("meta") ||
                lowerType.Contains("layout") ||
                lowerType.Contains("form") ||
                lowerType.Contains("metadata") ||
                lowerType.Contains("control") ||
                lowerType.Contains("artech.genexus");

            return strongHint;
        }

        private static int ScoreSourcePath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return 0;

            string p = sourcePath.ToLowerInvariant();
            int score = 0;
            if (p.Contains("layout")) score += 20;
            if (p.Contains("form")) score += 15;
            if (p.Contains("control")) score += 20;
            if (p.Contains("metadata")) score += 8;
            if (p.Contains("xml")) score += 12;
            return score;
        }

        private static bool TryPersistViaSourcePath(object root, string sourcePath, string normalizedXml)
        {
            if (root == null || string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            var target = ResolveSourcePathOwner(root, sourcePath);
            if (target == null)
            {
                return false;
            }

            if (TryInvokeXmlSetterMethods(target, normalizedXml))
            {
                return true;
            }

            return TrySetXmlLikeProperty(target, normalizedXml);
        }

        private static object ResolveSourcePathOwner(object root, string sourcePath)
        {
            var segments = sourcePath.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            int start = 0;
            if (string.Equals(segments[0], root.GetType().Name, StringComparison.OrdinalIgnoreCase))
            {
                start = 1;
            }

            object current = root;
            for (int i = start; i < segments.Length - 1; i++)
            {
                string segment = segments[i];
                if (segment.EndsWith("()", StringComparison.Ordinal))
                {
                    break;
                }

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var type = current.GetType();

                var prop = type.GetProperty(segment, flags);
                if (prop != null && prop.CanRead)
                {
                    try
                    {
                        current = prop.GetValue(current);
                        if (current == null) return null;
                        continue;
                    }
                    catch
                    {
                        return null;
                    }
                }

                var field = type.GetField(segment, flags);
                if (field != null)
                {
                    try
                    {
                        current = field.GetValue(current);
                        if (current == null) return null;
                        continue;
                    }
                    catch
                    {
                        return null;
                    }
                }

                return null;
            }

            return current;
        }

        private static bool TryInvokeXmlSetterMethods(object target, string normalizedXml)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();

            string[] prioritized = { "DeserializeFromXml", "LoadFromXml", "ApplyXml", "SetXml", "SetLayoutXml", "SetSource" };
            foreach (var methodName in prioritized)
            {
                var method = type
                    .GetMethods(flags)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, methodName, StringComparison.Ordinal) &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(string));
                if (method == null) continue;

                try
                {
                    method.Invoke(target, new object[] { normalizedXml });
                    return true;
                }
                catch
                {
                }
            }

            foreach (var method in type.GetMethods(flags))
            {
                if (method.IsSpecialName) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string)) continue;

                string name = method.Name.ToLowerInvariant();
                bool looksLikeSetter =
                    (name.Contains("set") || name.Contains("load") || name.Contains("apply") || name.Contains("deserialize")) &&
                    (name.Contains("xml") || name.Contains("layout") || name.Contains("source"));
                if (!looksLikeSetter) continue;

                try
                {
                    method.Invoke(target, new object[] { normalizedXml });
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySetXmlLikeProperty(object target, string normalizedXml)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanWrite || prop.PropertyType != typeof(string)) continue;

                string name = prop.Name.ToLowerInvariant();
                bool looksLikeXml =
                    name.Contains("xml") || name.Contains("layout") || name.Contains("source") || name.Contains("metadata") || name.Contains("content");
                if (!looksLikeXml) continue;

                try
                {
                    prop.SetValue(target, normalizedXml);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static XDocument TryParseCandidateXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            string trimmed = xml.TrimStart();
            if (!trimmed.StartsWith("<", StringComparison.Ordinal)) return null;

            try
            {
                return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch
            {
                return null;
            }
        }

        private static int ScoreVisualXml(XDocument doc)
        {
            if (doc?.Root == null) return 0;

            int score = 0;
            string root = doc.Root.Name.LocalName;
            int totalNodes = doc.Descendants().Count();
            score += totalNodes;

            if (!string.Equals(root, "Properties", StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }

            int controlAttrs = doc.Descendants().Count(e => e.Attribute("ControlName") != null);
            int captionAttrs = doc.Descendants().Count(e => e.Attribute("Caption") != null);
            int internalNameAttrs = doc.Descendants().Count(e => e.Attribute("InternalName") != null);
            score += controlAttrs * 200;
            score += captionAttrs * 40;
            score += internalNameAttrs * 40;

            return score;
        }

        private static ParseResult ParseVisualXml(string xml, string target, string emptyMessage, string emptyDetails)
        {
            if (string.IsNullOrWhiteSpace(xml))
            {
                return ParseResult.FromError(Models.McpResponse.Err(
                    code: "VisualXmlEmpty",
                    message: emptyMessage,
                    hint: emptyDetails,
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses which visual parts are available for this object.")),
                    target: target));
            }

            try
            {
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                if (doc.Root == null)
                {
                    // The envelope moved to LayoutService.InvalidVisualXml, shared with GetTree and
                    // FindControls. This copy had drifted: it dropped "or inspecting
                    // the part directly" from the hint and reworded the recovery step
                    // behind the same code.
                    return ParseResult.FromError(InvalidVisualXml(target));
                }

                return ParseResult.FromDocument(doc);
            }
            catch (Exception ex)
            {
                return ParseResult.FromError(Models.McpResponse.Err(
                    code: "InvalidVisualXml",
                    message: "Invalid visual XML: " + ex.Message,
                    hint: "The XML could not be parsed; the object's layout may contain a syntax error.",
                    nextSteps: new JArray(Models.McpResponse.NextStep("genexus_layout", new JObject { ["action"] = "inspect_surface", ["name"] = target }, "Diagnoses available visual surfaces for this object.")),
                    target: target));
            }
        }
    }
}
