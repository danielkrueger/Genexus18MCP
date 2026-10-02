using System.IO;
using System.Xml;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The one reader used to parse XML whose text comes out of a Knowledge Base or
    /// an imported archive: <c>knowledgebase.connection</c>, SDT structure blobs,
    /// WebForm part documents and module package manifests.
    ///
    /// <para>
    /// The rule this class exists to hold in one place is <c>DtdProcessing.Prohibit</c>
    /// plus <c>XmlResolver = null</c>. It was already written out correctly at eight
    /// call sites, and four more call sites parsed the same kind of input without it -
    /// which is the argument for this class rather than a fourth copy of the settings:
    /// <c>LogRedaction</c> makes exactly this case in its own doc comment. Five copies
    /// of one redaction rule existed there, and the one that had added a key on its own
    /// meant a connection string written through any of the other four was masked only
    /// in part. A rule copied into several call sites is only as good as the copy whose
    /// author remembered to update it; a rule with one definition cannot drift.
    /// </para>
    ///
    /// <para>
    /// The four documents are machine-generated GeneXus metadata and none of them has a
    /// legitimate DTD, so prohibiting DTDs rejects only content that should never be
    /// there. Every caller keeps its own failure behaviour: a rejected parse surfaces
    /// through the caller's existing <c>catch</c>, at that caller's own log level, with
    /// that caller's own degraded return.
    /// </para>
    ///
    /// <para>
    /// A new KB-sourced parse belongs here rather than in a new <c>XmlDocument</c>.
    /// <c>KbSourcedXmlDtdTests.EveryKbSourcedXmlLoadProhibitsDtds</c> guards the files
    /// already converted; a fifth file is not covered by it, so add the file to that
    /// test's list in the same change.
    /// </para>
    /// </summary>
    internal static class SafeXml
    {
        /// <summary>
        /// Ceiling on the characters a single KB-sourced document may expand to.
        /// DTDs are prohibited, so expansion comes from the document text itself and
        /// this is a size guard, not an entity-expansion budget.
        /// </summary>
        /// <remarks>
        /// Twice <c>ModuleInstallPackage.MaxXmlCharacters</c> (32 MiB), the bound the
        /// OPC manifest reader already used. None of these four documents is
        /// individually bounded today, so this is not a tightening of an existing
        /// limit for them - the real sizes could not be measured without a KB, and
        /// picking a value close to a real one would reject a legitimate large form.
        /// Sitting above the largest bound already in force in the Worker means this
        /// helper cannot become the reason a document that used to load now fails.
        /// </remarks>
        internal const long MaxCharacters = 64 * 1024 * 1024;

        /// <summary>
        /// Reads a document from a file, with DTD processing prohibited and no
        /// resolver, so no external entity is fetched. Throws what
        /// <see cref="XmlDocument"/> would have thrown for a malformed document, plus
        /// <see cref="XmlException"/> for a <c>DOCTYPE</c>.
        /// </summary>
        internal static XmlDocument LoadFile(string path)
        {
            var document = new XmlDocument();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (XmlReader reader = Create(stream))
                document.Load(reader);
            return document;
        }

        /// <summary>
        /// Reads a document already held as text, with the same settings as
        /// <see cref="LoadFile"/>. The text is routed through a reader rather than
        /// <c>XmlDocument.LoadXml</c> because that overload takes no settings and so
        /// could not carry <see cref="MaxCharacters"/> either.
        /// </summary>
        internal static XmlDocument LoadText(string xml)
        {
            var document = new XmlDocument();
            using (XmlReader reader = Create(new StringReader(xml)))
                document.Load(reader);
            return document;
        }

        private static XmlReader Create(Stream stream)
        {
            return XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxCharacters,
                CloseInput = false
            });
        }

        private static XmlReader Create(TextReader text)
        {
            return XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxCharacters,
                CloseInput = false
            });
        }
    }
}