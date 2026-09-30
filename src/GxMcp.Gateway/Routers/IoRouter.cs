using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Routers
{
    /// <summary>Typed domain routes extracted from the legacy operations router.</summary>
    public sealed class IoRouter : IMcpModuleRouter
    {
        public string ModuleName => "Operations";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_io": return ConvertIoUmbrella(args);
                default: return null;
            }
        }

        private object? ConvertIoUmbrella(JObject? args)
        {
            string? action = args?["action"]?.ToString();
            switch (action)
            {
                case "asset_find":
                case "asset_read":
                case "asset_write":
                {
                    var inner = action switch
                    {
                        "asset_find" => "Find",
                        "asset_read" => "Read",
                        _ => "Write"
                    };
                    return new
                    {
                        module = "Asset",
                        action = inner,
                        target = args?["path"]?.ToString(),
                        pattern = args?["pattern"]?.ToString(),
                        relativeRoot = args?["relativeRoot"]?.ToString(),
                        limit = args?["limit"]?.ToObject<int?>(),
                        includeContent = args?["includeContent"]?.ToObject<bool?>(),
                        maxBytes = args?["maxBytes"]?.ToObject<int?>(),
                        contentBase64 = args?["contentBase64"]?.ToString()
                    };
                }

                case "read_blob":
                    return new
                    {
                        module = "Object",
                        action = "ReadBlob",
                        target = args?["name"]?.ToString(),
                        outputPath = args?["outputPath"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        type = args?["type"]?.ToString(),
                        maxBytes = args?["maxBytes"]?.ToObject<int?>(),
                        includeBase64 = args?["includeBase64"]?.ToObject<bool?>() ?? false,
                        overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false
                    };

                case "export_part":
                    return new
                    {
                        module = "Object",
                        action = "ExportText",
                        target = args?["name"]?.ToString(),
                        outputPath = args?["outputPath"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        type = args?["type"]?.ToString(),
                        overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false
                    };

                case "import_part":
                    return new
                    {
                        module = "Object",
                        action = "ImportText",
                        target = args?["name"]?.ToString(),
                        inputPath = args?["inputPath"]?.ToString(),
                        part = args?["part"]?.ToString(),
                        type = args?["type"]?.ToString(),
                        dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false,
                        forceSave = args?["forceSave"]?.ToObject<bool?>() ?? false
                    };

                case "export_kb_to_text":
                    return new { module = "Object", action = "ExportTextBatch", target = args?["name"]?.ToString(), @params = args };

                case "import_text_to_kb":
                    return new { module = "Object", action = "ImportTextBatch", target = args?["name"]?.ToString(), @params = args };

                case "validate_kb_text_files":
                    return new { module = "Object", action = "ValidateTextBatch", target = args?["name"]?.ToString(), @params = args };

                case "validate_text_in_memory":
                    return new { module = "Object", action = "ValidateTextInMemory", @params = args };

                case "list_text_files":
                    return new { module = "Object", action = "ListTextInMemory", @params = args };

                case "text_mirror_start":
                    return new { module = "Object", action = "TextMirrorStart", @params = args };
                case "text_mirror_stop":
                    return new { module = "Object", action = "TextMirrorStop", @params = args };
                case "text_mirror_status":
                    return new { module = "Object", action = "TextMirrorStatus", @params = args };
                case "text_mirror_catchup":
                    return new { module = "Object", action = "TextMirrorCatchup", @params = args };
                case "text_mirror_set_references":
                    return new { module = "Object", action = "TextMirrorSetReferences", @params = args };

                case "delete_kb_objects":
                    return new { module = "Object", action = "DeleteTextBatch", target = args?["name"]?.ToString(), @params = args };

                case "export_unified":
                    return new
                    {
                        module = "Export",
                        action = "Unified",
                        target = args?["name"]?.ToString(),
                        @params = new JObject { ["type"] = args?["type"]?.ToString() }
                    };

                case "screenshot_publish":
                    return new { module = "ScreenshotPublish", action = "Publish", path = args?["path"]?.ToString() };

                case "ocr":
                    return new { module = "Ocr", action = "Run", path = args?["path"]?.ToString() };

                default:
                    return new
                    {
                        module = "Error",
                        action = "InvalidAction",
                        error = $"genexus_io: unknown action '{action}'. Valid: asset_find|asset_read|asset_write|read_blob|export_part|import_part|export_kb_to_text|import_text_to_kb|validate_kb_text_files|validate_text_in_memory|list_text_files|text_mirror_start|text_mirror_stop|text_mirror_status|text_mirror_catchup|text_mirror_set_references|delete_kb_objects|export_unified|screenshot_publish|ocr."
                    };
            }
        }

    }
}
