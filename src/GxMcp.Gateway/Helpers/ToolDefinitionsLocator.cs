using System.IO;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Finds <c>tool_definitions.json</c>, which is the source of truth for the
    /// tool schemas: <c>tool_definitions.json</c> defines them,
    /// <c>GatewayArgsValidator</c> validates incoming arguments against them, and
    /// <c>ToolIdentity</c> decides which names are canonical.
    ///
    /// It existed twice, code-identical, as a private
    /// <c>LocateToolDefinitions</c> on <c>GatewayArgsValidator</c> and on
    /// <c>ToolIdentity</c> - <c>ToolIdentity</c>'s own comment said it mirrored the
    /// other, which is an admission that the two have to stay in step. A schema
    /// source of truth located two ways is a validator and an identity check that
    /// can disagree about which file they are reading.
    ///
    /// <para><c>McpRouter</c> loads the same file by a third strategy and is
    /// deliberately left alone: it looks only beside the executing assembly, with
    /// no upward walk, and pointing it at this would change where it finds the file
    /// in layouts where it currently reports none. See the note there.</para>
    /// </summary>
    internal static class ToolDefinitionsLocator
    {
        /// <summary>
        /// The path to <c>tool_definitions.json</c> next to the running assembly,
        /// or null when it is not found.
        /// </summary>
        internal static string? Locate() => LocateFrom(AppContext.BaseDirectory);

        /// <summary>
        /// <see cref="Locate"/>, with the directory the search starts from supplied
        /// rather than taken from the assembly.
        ///
        /// The search root is a parameter because the upward walk is otherwise
        /// untestable: the test project copies the JSON next to its own binary, so
        /// the first candidate always wins and the walk never runs in a test. That
        /// made the half that actually needed coverage - the half that was
        /// duplicated - the half with none.
        /// </summary>
        internal static string? LocateFrom(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) return null;

            // 1. Beside the assembly (deployed layout)
            string beside = Path.Combine(baseDirectory, "tool_definitions.json");
            if (File.Exists(beside)) return beside;

            // 2. Walk up from base dir (IDE test / dev layout)
            string dir = baseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string c1 = Path.Combine(dir, "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(c1)) return c1;
                string c2 = Path.Combine(dir, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(c2)) return c2;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }
    }
}