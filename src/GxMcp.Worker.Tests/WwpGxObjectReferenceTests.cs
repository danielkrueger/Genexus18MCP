using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #414: gxobject is <type GUID>-<qualified name>, as the IDE writes it.
    public class WwpGxObjectReferenceTests
    {
        private const string ProcedureType = "84a12160-f59b-4ad7-a683-ea4481ac23e9";

        [Fact]
        public void ModuleObjectUsesQualifiedName()
            => Assert.Equal(ProcedureType + "-Sales.PostOrder",
                WwpActionService.FormatGxObjectReference(ProcedureType, "Sales.PostOrder", "PostOrder"));

        [Fact]
        public void RootModuleObjectUsesName()
        {
            Assert.Equal(ProcedureType + "-PostOrder", WwpActionService.FormatGxObjectReference(ProcedureType, "PostOrder", "PostOrder"));
            Assert.Equal(ProcedureType + "-PostOrder", WwpActionService.FormatGxObjectReference(ProcedureType, null, "PostOrder"));
        }
    }
}
