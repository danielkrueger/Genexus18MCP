using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // issue #419: the terse projection must keep the root SDK exception type and trace.
    public class TerseErrorSdkCauseTests
    {
        [Fact]
        public void KeepsExceptionTypeAndFailureTrace()
        {
            var input = JObject.Parse(@"{status:'error',error:{code:'WwpFormActionFailed',message:'Unable to cast String to KBObject'},
                exceptionType:'InvalidCastException',failureTrace:'A.B <- C.D',stack:'private'}");
            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);
            Assert.Equal("InvalidCastException", (string?)output["exceptionType"]);
            Assert.Equal("A.B <- C.D", (string?)output["failureTrace"]);
            Assert.Null(output["stack"]);
        }
    }
}
