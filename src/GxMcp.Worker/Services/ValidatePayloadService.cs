using System;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class ValidatePayloadService
    {
        private readonly ObjectService _objectService;

        public ValidatePayloadService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string Validate(string target, string partName, string payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(payload))
                    return Error("Empty payload");

                var obj = _objectService.FindObject(target);
                if (obj == null) return Error("Object not found: " + target);

                var result = new JObject
                {
                    ["target"] = target,
                    ["part"] = partName,
                };

                try { XDocument.Parse(payload, LoadOptions.PreserveWhitespace); }
                catch (Exception ex)
                {
                    return Error("Payload is not well-formed XML: " + ex.Message);
                }

                var suspects = WebFormSchemaHints.ScanForRejectedAttributes(payload);
                if (suspects.Count > 0)
                {
                    var arr = new JArray();
                    foreach (var s in suspects)
                    {
                        var entry = new JObject
                        {
                            ["element"] = s.Element,
                            ["attribute"] = s.Attribute,
                            ["reason"] = s.Reason
                        };
                        // Issue #360: the actionable half. "This attribute is not in the
                        // hint table" leaves the caller guessing; naming the attribute to
                        // write instead is what turns a warning into a fix.
                        if (!string.IsNullOrEmpty(s.Fix)) entry["fix"] = s.Fix;
                        arr.Add(entry);
                    }
                    result["preflightWarnings"] = arr;
                    result["hasWarnings"] = true;
                }

                try
                {
                    string currentXml = null;
                    if (WebFormXmlHelper.IsVisualPart(partName))
                        currentXml = WebFormXmlHelper.ReadEditableXml(obj);
                    if (!string.IsNullOrEmpty(currentXml))
                    {
                        if (XmlEquivalence.AreEquivalent(currentXml, payload, out _, out var diff))
                        {
                            result["wouldChange"] = false;
                        }
                        else
                        {
                            result["wouldChange"] = true;
                            if (diff != null)
                            {
                                var d = new JObject();
                                if (!string.IsNullOrEmpty(diff.ElementName)) d["element"] = diff.ElementName;
                                if (!string.IsNullOrEmpty(diff.Path)) d["path"] = diff.Path;
                                if (!string.IsNullOrEmpty(diff.Summary)) d["summary"] = diff.Summary;
                                result["diff"] = d;
                            }
                        }
                    }
                }
                catch { /* current-state read is best-effort */ }

                return McpResponse.Ok(code: "PayloadValid", result: result);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        private static string Error(string msg) => McpResponse.Err(code: "PayloadValidationFailed", message: msg);
    }
}
