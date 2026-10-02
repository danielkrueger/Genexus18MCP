using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The key alternation used to be wrapped in <c>\b</c> on both sides, so it
    /// demanded a word boundary <i>before</i> the key. A compound key whose prefix
    /// is a letter - <c>KBDbPassword=</c>, <c>DbPassword=</c>, <c>myToken=</c> -
    /// and the standard ADO.NET short alias <c>Pwd=</c> therefore fell straight
    /// through and were logged intact. Five call sites write through this one rule,
    /// so five log paths were exposed; the gap was known and recorded in
    /// <see cref="LogRedactionSingleSourceTests"/> rather than left in a tracker.
    ///
    /// <see cref="ANearMissIsNotMasked"/> is the half that matters most. Widening
    /// the key group is only safe while the prefix class stays narrow and
    /// <c>pass</c> keeps its strict boundary. Without that guard, closing the leak
    /// by folding <c>pass</c> into the prefixed group would match <c>bypass=1</c>
    /// as <c>by</c> + <c>pass</c> and corrupt ordinary diagnostics - trading a
    /// leak for a much quieter log.
    /// </summary>
    public class LogRedactionKeyShapeTests
    {
        private const string Sentinel = "SENTINEL-CREDENTIAL";

        [Theory]
        [InlineData("/p:KBDbPassword=" + Sentinel)]
        [InlineData("KBDbPassword=" + Sentinel)]
        [InlineData("DbPassword=" + Sentinel)]
        [InlineData("Pwd=" + Sentinel)]
        [InlineData("Server=db;Pwd=" + Sentinel)]
        [InlineData("myToken=" + Sentinel)]
        public void ACompoundKeyIsMasked(string input)
        {
            // These six shapes all passed through the old bare \b key untouched.
            string masked = LogRedaction.Redact(input);

            Assert.DoesNotContain(Sentinel, masked);
            Assert.Contains("<redacted>", masked);
        }

        [Theory]
        [InlineData("bypass=" + Sentinel)]
        [InlineData("compassion=" + Sentinel)]
        [InlineData("aPass=" + Sentinel)]
        [InlineData("the password is wrong")]
        [InlineData("passwords must be long")]
        [InlineData("the token is invalid")]
        [InlineData(Sentinel)]
        public void ANearMissIsNotMasked(string input)
        {
            // Over-redaction costs diagnostics just as surely as a leak costs
            // security, so a key-shaped word that is not a key has to come back
            // byte for byte. The first three fail the moment "pass" is given the
            // prefix allowance the other keys now have.
            Assert.Equal(input, LogRedaction.Redact(input));
        }

        [Theory]
        [InlineData("Password=" + Sentinel)]
        [InlineData("password: " + Sentinel)]
        [InlineData("passwd=" + Sentinel)]
        [InlineData("token=" + Sentinel)]
        [InlineData("secret=" + Sentinel)]
        [InlineData("api_key=" + Sentinel)]
        [InlineData("authorization=Bearer " + Sentinel)]
        [InlineData("credential=" + Sentinel)]
        [InlineData("connectionstring=" + Sentinel)]
        public void EveryPreviouslyMaskedKeyIsStillMasked(string input)
        {
            // A replay of the surface the prefix allowance was added next to, so
            // this file states the whole key shape in one place: the six newly
            // matched shapes above, and the nine that already worked.
            string masked = LogRedaction.Redact(input);

            Assert.DoesNotContain(Sentinel, masked);
            Assert.Contains("<redacted>", masked);
        }
    }
}
