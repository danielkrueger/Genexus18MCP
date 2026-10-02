using System;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    // issue #39 follow-up: write attribute-level properties that the structure DSL doesn't reach
    // (Formula, subtype, Title/ColumnTitle, IsCollection, DomainBasedOn). GeneXus attributes are
    // KB-global objects, so this edits the attribute everywhere it is used.
    public class AttributeWriteService
    {
        private readonly ObjectService _objectService;

        public AttributeWriteService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        // payload = { title?, columnTitle?, contextualTitle?, isCollection?, formula?, subtypeOf?, basedOnDomain? }
        public string SetAttributeProperties(string attrName, string payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(attrName)) return Models.McpResponse.Err(
                    code: "InvalidAttribute", message: "Attribute name (name) is required.", target: attrName);
                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidPayload", message: "payload is required.",
                    hint: "e.g. { \"formula\": \"CustomerBalance = sum(InvoiceAmount)\" } or { \"subtypeOf\": \"CustomerId\" } or { \"title\": \"...\" }.",
                    target: attrName);

                // GetKB() is declared dynamic; type the model explicitly so downstream calls
                // (Attribute.Get, EnsureSave extension) bind statically instead of via the
                // runtime binder (extension methods are invisible to dynamic dispatch).
                Artech.Architecture.Common.Objects.KBModel model =
                    _objectService.GetKbService().GetKB().DesignModel;
                var attr = Artech.Genexus.Common.Objects.Attribute.Get(model, attrName);
                if (attr == null) return Models.McpResponse.Err(
                    code: "AttributeNotFound",
                    message: $"Attribute '{attrName}' does not exist in the KB.",
                    hint: "Attributes are created inside a Transaction structure. Add it via genexus_edit part=Structure first.",
                    target: attrName);

                var json = JObject.Parse(payload);
                var applied = new JArray();

                using (var sdkTrans = model.KB.BeginTransaction())
                {
                    try
                    {
                        if (json["title"] != null) { attr.Title = json["title"].ToString(); applied.Add("title"); }
                        if (json["columnTitle"] != null) { attr.ColumnTitle = json["columnTitle"].ToString(); applied.Add("columnTitle"); }
                        if (json["contextualTitle"] != null) { attr.ContextualTitleProperty = json["contextualTitle"].ToString(); applied.Add("contextualTitle"); }
                        if (json["isCollection"] != null) { attr.IsCollection = json["isCollection"].ToObject<bool>(); applied.Add("isCollection"); }

                        if (json["basedOnDomain"] != null)
                        {
                            string dn = json["basedOnDomain"].ToString();
                            var dom = _objectService.FindObject(dn) as Domain;
                            if (dom == null) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "DomainNotFound", message: $"Domain '{dn}' not found.", target: attrName); }
                            attr.DomainBasedOn = dom; applied.Add("basedOnDomain");
                        }

                        if (json["subtypeOf"] != null)
                        {
                            string sn = json["subtypeOf"].ToString();
                            var super = Artech.Genexus.Common.Objects.Attribute.Get(model, sn);
                            if (super == null) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "SupertypeNotFound",
                                message: $"Supertype attribute '{sn}' not found.",
                                hint: "A subtype must point at an existing base (super) attribute.",
                                target: attrName); }
                            attr.SuperType = super; applied.Add("subtypeOf");
                        }

                        if (json["formula"] != null)
                        {
                            string fstr = json["formula"].ToString();
                            try
                            {
                                var f = Formula.Parse(fstr, attr, null);
                                attr.Formula = f;
                                applied.Add("formula");
                            }
                            catch (Exception fex)
                            {
                                try { sdkTrans.Rollback(); } catch { }
                                return Models.McpResponse.Err(
                                    code: "FormulaParseFailed",
                                    message: $"Could not parse formula: {fex.Message}",
                                    hint: "Use GeneXus formula syntax, e.g. 'sum(InvoiceAmount)' or a conditional formula. Attribute references must exist.",
                                    target: attrName);
                            }
                        }

                        if (applied.Count == 0) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                            code: "NoPropertiesToApply",
                            message: "payload contained no recognized attribute properties.",
                            hint: "Recognized: title, columnTitle, contextualTitle, isCollection, formula, subtypeOf, basedOnDomain.",
                            target: attrName); }

                        attr.EnsureSave();
                        sdkTrans.Commit();
                        WriteService.NotePerTargetWrite(attrName);

                        return Models.McpResponse.Ok(
                            target: attrName,
                            code: "AttributeUpdated",
                            result: new JObject { ["attribute"] = attr.Name, ["applied"] = applied });
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "AttributeUpdateFailed",
                            message: ex.Message,
                            hint: "Check the worker log for the SDK stack trace.",
                            target: attrName,
                            extra: new JObject { ["stackTrace"] = ex.StackTrace });
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "AttributeUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the attribute exists and payload is valid JSON.",
                    target: attrName);
            }
        }
    }
}
