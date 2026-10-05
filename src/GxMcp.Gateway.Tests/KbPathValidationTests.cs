using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // issue #38 defect #1: genexus_kb action=open must reject a path that is not a real
    // KB root BEFORE a worker is spawned for it, so a GeneXus environment/model subfolder
    // (no .gxw / no knowledgebase.connection) never triggers the endless background
    // auto-open retry loop that wedged the gateway.
    public class KbPathValidationTests
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxmcp-kbval-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void IsPlausibleKbPath_KbFolderWithGxw_IsAccepted()
        {
            string kbDir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(kbDir, "Demo.gxw"), "");
                Assert.True(Configuration.IsPlausibleKbPath(kbDir));
            }
            finally { Directory.Delete(kbDir, true); }
        }

        [Fact]
        public void IsPlausibleKbPath_KbFolderWithLegacyConnection_IsAccepted()
        {
            string kbDir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(kbDir, "knowledgebase.connection"), "");
                Assert.True(Configuration.IsPlausibleKbPath(kbDir));
            }
            finally { Directory.Delete(kbDir, true); }
        }

        [Fact]
        public void IsPlausibleKbPath_DirectGxwFile_IsAccepted()
        {
            string kbDir = NewTempDir();
            try
            {
                string gxw = Path.Combine(kbDir, "Demo.gxw");
                File.WriteAllText(gxw, "");
                Assert.True(Configuration.IsPlausibleKbPath(gxw));
            }
            finally { Directory.Delete(kbDir, true); }
        }

        [Fact]
        public void IsPlausibleKbPath_EnvironmentSubfolder_IsRejected()
        {
            // The exact scenario from issue #38: an environment subfolder with no .gxw and
            // no knowledgebase.connection.
            string kbDir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(kbDir, "Demo.gxw"), "");
                string envDir = Path.Combine(kbDir, "Desenv");
                Directory.CreateDirectory(envDir);
                File.WriteAllText(Path.Combine(envDir, "model.ini"), "");
                Assert.False(Configuration.IsPlausibleKbPath(envDir));
            }
            finally { Directory.Delete(kbDir, true); }
        }

        [Fact]
        public void IsPlausibleKbPath_MissingPath_IsRejected()
        {
            Assert.False(Configuration.IsPlausibleKbPath(Path.Combine(Path.GetTempPath(), "gxmcp-does-not-exist-" + Guid.NewGuid().ToString("N"))));
            Assert.False(Configuration.IsPlausibleKbPath(""));
            Assert.False(Configuration.IsPlausibleKbPath(null!));
        }
    }
}
