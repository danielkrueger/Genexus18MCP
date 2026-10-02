using System;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Descriptors;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class ForgeService
    {
        private readonly KbService _kbService;

        public ForgeService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string Scaffold(string type, string name, JObject properties)
        {
            try
            {
                var kb = _kbService.GetKB();
                Logger.Info(string.Format("Scaffolding new {0}: {1}", type, name));
                Logger.Info("[DEBUG-SCAFFOLD] Step 1: Init");

                KBObject obj = null;
                if (type.Equals("Procedure", StringComparison.OrdinalIgnoreCase) || type.Equals("Prc", StringComparison.OrdinalIgnoreCase))
                {
                    obj = KBObject.Create(kb.DesignModel, KBObjectDescriptor.Get<Procedure>().Id);
                }
                else if (type.Equals("Transaction", StringComparison.OrdinalIgnoreCase) || type.Equals("Trn", StringComparison.OrdinalIgnoreCase))
                {
                    obj = KBObject.Create(kb.DesignModel, KBObjectDescriptor.Get<Transaction>().Id);
                }
                else
                {
                    return Models.McpResponse.Err(code: "ScaffoldTypeUnsupported",
                        message: "Scaffold for type '" + type + "' is not implemented.",
                        hint: "Supported scaffold types are limited; create this object type with genexus_create instead.");
                }

                Logger.Info("[DEBUG-SCAFFOLD] Step 2: Instantiated");
                obj.Name = name;
                
                Logger.Info("[DEBUG-SCAFFOLD] Step 3: Name set: " + name);
                obj.Description = (properties != null && properties["description"] != null) 
                    ? properties["description"].ToString() 
                    : ("Created by MCP: " + name);
                
                Logger.Info("[DEBUG-SCAFFOLD] Step 4: Description set");
                // Add default logic based on properties
                if (obj is Procedure prc)
                {
                    var sourcePart = prc.Parts.Get<SourcePart>();
                    if (sourcePart != null)
                    {
                        string codeParams = (properties != null && properties["code"] != null) ? properties["code"].ToString() : "";
                        string literalLineBreakError = TextPayloadGuard.BuildWriteError(name, "Source", "properties.code", codeParams);
                        if (literalLineBreakError != null) return literalLineBreakError;
                        string source = "// Template created by MCP\r\n" + codeParams;
                        sourcePart.Source = source;
                    }
                }
                
                Logger.Info("[DEBUG-SCAFFOLD] Step 5: Before Save");
                obj.EnsureSave();
                WriteService.NotePerTargetWrite(name);
                Logger.Info($"Scaffold complete: {name}");
                return Models.McpResponse.Ok(
                    target: name,
                    code: "ScaffoldCompleted",
                    result: new JObject { ["name"] = name, ["guid"] = obj.Guid.ToString() });
            }
            catch (Exception ex)
            {
                Logger.Error($"Scaffold Error: {ex.Message}");
                return Models.McpResponse.Err(code: "ScaffoldFailed", message: ex.ToString());
            }
        }
    }
}
