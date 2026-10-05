using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #416: a restored part was reported as UndoFailed when the write answered
    // status "ok" together with a message, or an explicit null error.
    public class UndoWriteSuccessTests
    {
        [Theory]
        [InlineData("{\"status\":\"ok\",\"message\":\"Variables updated\"}")]
        [InlineData("{\"status\":\"OK\",\"error\":null}")]
        [InlineData("{\"status\":\"Success\",\"message\":\"x\"}")]
        [InlineData("{\"result\":1}")]
        public void Success(string json) => Assert.True(UndoService.IsWriteSuccess(JObject.Parse(json)));

        [Theory]
        [InlineData("{\"status\":\"error\"}")]
        [InlineData("{\"status\":\"partial\"}")]
        [InlineData("{\"status\":\"accepted\"}")]
        [InlineData("{\"error\":\"boom\"}")]
        [InlineData("{\"message\":\"boom\"}")]
        public void Failure(string json) => Assert.False(UndoService.IsWriteSuccess(JObject.Parse(json)));

        [Fact]
        public void NullResponseFails() => Assert.False(UndoService.IsWriteSuccess(null));
    }
}
