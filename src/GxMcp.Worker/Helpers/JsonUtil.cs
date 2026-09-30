using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    public static class JsonUtil
    {
        /// Returns parsed JToken, or a JValue wrapping the raw string when parse fails.
        /// JValue.CreateNull() for null/empty input. Never throws.
        public static JToken SafeParse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return JValue.CreateNull();
            try { return JToken.Parse(raw); }
            catch { return new JValue(raw); }
        }

        /// <summary>
        /// Whether the caller actually supplied this argument.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Not the same question as "is the token null", and the difference is a bug
        /// factory. For a <c>JObject</c> built from JSON, a property that is absent
        /// reads back as C# <c>null</c>, but a property the caller wrote as
        /// <c>"targets": null</c> reads back as a <c>JValue</c> of type
        /// <see cref="JTokenType.Null"/> - a live object that is not null. A guard
        /// written as <c>args["x"] != null</c> therefore takes its "supplied" branch
        /// for an explicit null, and the very next test - <c>is not JArray</c>, or
        /// <c>Type != JTokenType.Object</c> - is also true of that null, so the guard
        /// reports a malformed argument the caller never sent.
        /// </para>
        /// <para>
        /// Callers routinely do send them that way. An LLM composing a call from a tool
        /// schema frequently serialises every declared property and leaves the ones it
        /// is not using as explicit nulls, so "the caller did not pass this" and "the
        /// caller passed null" are indistinguishable in practice - and only one of them
        /// is what the schema's optional means.
        /// </para>
        /// <para>
        /// So an absent argument and an explicit null must take the same branch. Which
        /// branch that is depends on the argument, and for these it is the absent one:
        /// a null is not a value the caller supplied, and nothing downstream should act
        /// on it as though it were.
        /// </para>
        /// </remarks>
        public static bool IsSupplied(JToken token) =>
            token != null && token.Type != JTokenType.Null;
    }
}
