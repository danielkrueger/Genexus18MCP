using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// <c>genexus_analyze mode=theme_classes</c> (roadmap W6). Read-only, KB-wide.
    ///
    /// An agent authoring a layout previously had to guess a class name, and nothing in
    /// the tool surface listed the classes a KB defines. This is a separate service
    /// rather than another mode of the control catalog on purpose: the control list
    /// comes from <c>IUserControlsManagerService</c> and this comes from the search
    /// index, so folding one into the other would make the response shape depend on a
    /// boolean and leave the service answering two unrelated questions.
    /// </summary>
    public class ThemeClassCatalogService
    {
        private readonly ObjectService _objects;

        public ThemeClassCatalogService(ObjectService objects)
        {
            _objects = objects;
        }

        public string Run(JObject args)
        {
            int limit = args?["limit"]?.ToObject<int?>() ?? 200;
            if (limit <= 0) limit = 200;

            // The index is the only complete enumeration available headlessly: a direct
            // KBModel walk and a theme's own style tree each surface a single theme-root
            // object, and serving that one entry as a catalog would look complete.
            var catalog = ThemeClassCatalog.BuildFromIndex(_objects?.GetLoadedIndexOrNull(), limit);

            var classes = new JArray();
            foreach (var c in catalog.Classes)
            {
                classes.Add(new JObject
                {
                    ["name"] = c.Name,
                    ["objectGuid"] = c.ObjectGuid
                });
            }

            return McpResponse.Ok(
                code: catalog.UnavailableReason == null ? "ThemeClassesRetrieved" : "ThemeClassesUnavailable",
                result: new JObject
                {
                    ["count"] = classes.Count,
                    ["truncated"] = catalog.TotalCount > classes.Count,
                    ["themeClasses"] = classes,
                    ["authorableInLayoutClassAttribute"] = false,
                    ["controlTypesAvailable"] = false,
                    ["reason"] = catalog.UnavailableReason,
                    ["hint"] = catalog.UnavailableReason != null
                        ? "The class catalog needs the KB index. Run genexus_lifecycle action=index, then retry."
                        : "Use this to choose a class by name. objectGuid is the ThemeClass KB object's GUID and is NOT the value a layout class attribute takes: a measured layout attribute (<guid>-<suffix>) matched none of the KB's class GUIDs, so it is an SDK style reference this Server cannot resolve headlessly. Authoring a layout class stays on the layout-document path; a wrong reference silently loses styling."
                });
        }
    }
}
