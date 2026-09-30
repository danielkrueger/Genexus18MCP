using System;
using System.IO;
using System.Xml;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Builds the SQL connection string for a Knowledge Base from its
    /// <c>knowledgebase.connection</c> file, or null when that file is absent,
    /// unreadable, or names no server/database.
    ///
    /// This existed twice — once in SdtModelPropagation, once in
    /// WebFormCompositionRepair — and both copies read a connection string out of
    /// the KB. The <c>IntegratedSecurity</c> branch decides whether a login is sent
    /// to the server, so that branch is worth having exactly one copy: two
    /// implementations are two chances to disagree about it, and a caller that
    /// silently connects under different credentials is not a failure anyone
    /// would trace back here.
    ///
    /// Callers still own the "unresolved" log, because each frames the skip in
    /// terms of its own operation.
    /// </summary>
    internal static class KbConnectionString
    {
        internal const string ConnectionFileName = "knowledgebase.connection";

        /// <summary>
        /// <paramref name="logTag"/> identifies the caller in the parse-failure log
        /// (e.g. "SDT-PROP"). Malformed KB metadata is reported at Info so neither
        /// original call site loses a line it used to emit.
        /// </summary>
        internal static string Build(string kbPath, string logTag)
        {
            try
            {
                string connFile = Path.Combine(kbPath, ConnectionFileName);
                if (!File.Exists(connFile)) return null;

                var doc = new XmlDocument();
                doc.Load(connFile);

                string server = doc.SelectSingleNode("/ConnectionInformation/ServerInstance")?.InnerText;
                string db = doc.SelectSingleNode("/ConnectionInformation/DBName")?.InnerText;
                string integrated = doc.SelectSingleNode("/ConnectionInformation/IntegratedSecurity")?.InnerText;

                if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(db)) return null;

                bool useSspi = string.Equals(integrated, "True", StringComparison.OrdinalIgnoreCase);
                return useSspi
                    ? $"Server={server};Database={db};Integrated Security=SSPI;TrustServerCertificate=true;Connection Timeout=5"
                    : $"Server={server};Database={db};TrustServerCertificate=true;Connection Timeout=5";
            }
            catch (Exception ex)
            {
                Logger.Info("[" + logTag + "] " + ConnectionFileName + " parse failed: " + ex.Message);
                return null;
            }
        }
    }
}
